using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class ProServerStatusParserTests
{
    private static readonly DateTimeOffset ResetA = DateTimeOffset.Parse("2026-09-06T05:20:13.110944+00:00");
    private static readonly DateTimeOffset ResetB = DateTimeOffset.Parse("2026-09-06T05:20:13.331007+00:00");
    private static readonly DateTimeOffset ResetC = DateTimeOffset.Parse("2026-09-06T05:20:13.358703+00:00");

    [Fact]
    public void ParsesLiveObservedProSlugsAndCorrelatesBlockedFeature()
    {
        var parsed = AccountParser.ParseQuotaMetadata(LiveShape());

        Assert.True(parsed.ProServerStatus.ServerObserved);
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, parsed.ProServerStatus.RestrictionState);
        Assert.Equal(ServerResetConfidence.Server, parsed.ProServerStatus.ResetConfidence);
        Assert.Equal(ResetC, parsed.ProServerStatus.ResetAt);
        Assert.Equal("reason", parsed.ProServerStatus.CorrelatedBlockedFeatureName);
        Assert.Equal(50, parsed.ProServerStatus.CorrelatedBlockedFeatureLimitHint);
        Assert.Null(parsed.ProServerStatus.BlockReason);
        Assert.Equal("server supplied restriction notice", parsed.ProServerStatus.RestrictionDescription);
        Assert.False(parsed.ProServerStatus.HasAmbiguousResets);
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, parsed.ProServerStatus.RestrictionState);
        Assert.Equal("P!", ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = parsed.ProServerStatus }).ProCompactToken);
        Assert.Equal(3, parsed.ProServerStatus.ModelLimits.Count);
        Assert.Contains(parsed.ProServerStatus.ModelLimits, limit => limit.Slug == "gpt-5-5-pro");
        Assert.Contains(parsed.ProServerStatus.ModelLimits, limit => limit.Slug == "gpt-5-6-pro");
        Assert.Contains(parsed.ProServerStatus.ModelLimits, limit => limit.Slug == "gpt-6-pro");
        Assert.False(parsed.MatchesGptProAllowance);
        Assert.Null(parsed.SharedProWeekly?.Used);
    }

    [Fact]
    public void NoUsedRemaining_DoesNotCreateAuthoritativeCount()
    {
        var metadata = AccountParser.ParseQuotaMetadata(LiveShape());
        var snapshot = new QuotaEngine().Build(
            [],
            AppSettings.CreateDefaults(),
            new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            new CoverageInfo { NormalChats = true },
            metadata,
            AppSyncStatus.UpToDate);

        Assert.False(snapshot.UsesServerCount);
        Assert.Equal(ResetAnchorSource.Server, snapshot.ResetAnchorSource);
        Assert.Equal(ResetC, snapshot.ResetAt);
        Assert.Equal(CoverageConfidence.Authoritative, snapshot.Coverage.ResetConfidence);
        Assert.NotEqual(CoverageConfidence.Authoritative, snapshot.Coverage.CountConfidence);
        Assert.False(ProStatusPresentation.From(snapshot).ExactRemainingAvailable);
        Assert.DoesNotContain("50", ProStatusPresentation.From(snapshot).ReconstructedText, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalResetChoosesLatestWithinTolerance()
    {
        var (_, confidence, ambiguous) = ProServerStatusParser.CorrelateResets([ResetA, ResetB, ResetC]);
        Assert.Equal(ServerResetConfidence.Server, confidence);
        Assert.False(ambiguous);
        Assert.Equal(ResetC, ProServerStatusParser.CorrelateResets([ResetA, ResetB, ResetC]).ResetAt);
    }

    [Fact]
    public void DivergentResetsAreAmbiguous()
    {
        var early = ResetA;
        var late = ResetA.AddMinutes(10);
        var result = ProServerStatusParser.CorrelateResets([early, late]);
        Assert.True(result.Ambiguous);
        Assert.Equal(ServerResetConfidence.Ambiguous, result.Confidence);
        Assert.Null(result.ResetAt);
    }

    [Fact]
    public void IgnoresImageGenAndDeepResearch()
    {
        Assert.False(ProServerStatusParser.IsRecognizedProModelSlug("image_gen"));
        Assert.False(ProServerStatusParser.IsRecognizedProModelSlug("deep_research"));
        Assert.False(ProServerStatusParser.IsRecognizedProModelSlug("codex"));
        Assert.False(ProServerStatusParser.IsRecognizedProModelSlug("gpt-5"));
        Assert.True(ProServerStatusParser.IsRecognizedProModelSlug("gpt-5-5-pro"));
        Assert.True(ProServerStatusParser.IsRecognizedProModelSlug("gpt-5-6-pro"));
        Assert.True(ProServerStatusParser.IsRecognizedProModelSlug("gpt-6-pro"));
        Assert.True(ProServerStatusParser.IsRecognizedProModelSlug("gpt-7-pro"));

        var parsed = ProServerStatusParser.Parse(new JsonObject
        {
            ["model_limits"] = new JsonArray
            {
                new JsonObject { ["model_slug"] = "image_gen", ["resets_after"] = "2026-09-06T05:20:13Z" },
                new JsonObject { ["model_slug"] = "deep_research", ["resets_after"] = "2026-09-06T05:20:13Z" }
            }
        });
        Assert.Empty(parsed.ModelLimits);
        Assert.True(parsed.ServerObserved);
        Assert.Equal(ProRestrictionState.Unknown, parsed.RestrictionState);
        Assert.Equal("P?", ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = parsed }).ProCompactToken);
    }

    [Fact]
    public void MissingOrEmptyModelLimits_AreUnknown()
    {
        foreach (var root in new JsonNode?[] { new JsonObject(), new JsonObject { ["model_limits"] = new JsonArray() } })
        {
            var parsed = ProServerStatusParser.Parse(root);
            Assert.True(parsed.ServerObserved);
            Assert.Empty(parsed.ModelLimits);
            Assert.Equal(ProRestrictionState.Unknown, parsed.RestrictionState);
            var presentation = ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = parsed });
            Assert.Equal("P?", presentation.ProCompactToken);
            Assert.Equal(UiText.Unavailable, presentation.ProStateText);
            Assert.False(presentation.ServerStatusKnown);
        }
    }

    [Fact]
    public void RecognizedProLimitWithoutBlock_IsObservedAvailable()
    {
        var parsed = ProServerStatusParser.Parse(new JsonObject
        {
            ["model_limits"] = new JsonArray
            {
                new JsonObject { ["model_slug"] = "gpt-6-pro", ["resets_after"] = "2026-09-06T05:20:13Z" }
            }
        });
        Assert.True(parsed.ServerObserved);
        Assert.Equal(ProRestrictionState.NoCorrelatedRestrictionObserved, parsed.RestrictionState);
        var presentation = ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = parsed });
        Assert.Equal("POK", presentation.ProCompactToken);
        Assert.Equal(UiText.ProNoServerRestriction, presentation.ProStateText);
        Assert.True(presentation.ServerStatusKnown);
    }

    [Fact]
    public void UnrelatedBlockedFeatureDoesNotCorrelate()
    {
        var parsed = ProServerStatusParser.Parse(new JsonObject
        {
            ["model_limits"] = new JsonArray
            {
                new JsonObject { ["model_slug"] = "gpt-6-pro", ["resets_after"] = "2026-09-06T05:20:13Z" }
            },
            ["blocked_features"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "image_gen",
                    ["limit"] = 5,
                    ["resets_after"] = "2026-09-08T00:00:00Z"
                }
            }
        });
        Assert.Equal(ProRestrictionState.NoCorrelatedRestrictionObserved, parsed.RestrictionState);
        Assert.Null(parsed.CorrelatedBlockedFeatureName);
        Assert.Null(parsed.CorrelatedBlockedFeatureLimitHint);
    }

    [Fact]
    public void NullBlockReasonRemainsNull()
    {
        var parsed = ProServerStatusParser.Parse(LiveShape());
        Assert.Null(parsed.BlockReason);
    }

    [Fact]
    public void LimitHintIsNotAuthoritativeAllowance()
    {
        var metadata = AccountParser.ParseQuotaMetadata(LiveShape());
        Assert.Equal(50, metadata.ProServerStatus.CorrelatedBlockedFeatureLimitHint);
        Assert.Null(metadata.SharedProWeekly);
        Assert.False(metadata.WeeklyWindow(SubscriptionPreset.Pro100)?.IsAuthoritative ?? false);
    }

    private static JsonObject LiveShape() => new()
    {
        ["model_limits"] = new JsonArray
        {
            new JsonObject { ["model_slug"] = "gpt-5-5-pro", ["resets_after"] = "2026-09-06T05:20:13.110944+00:00" },
            new JsonObject { ["model_slug"] = "gpt-5-6-pro", ["resets_after"] = "2026-09-06T05:20:13.331007+00:00" },
            new JsonObject { ["model_slug"] = "gpt-6-pro", ["resets_after"] = "2026-09-06T05:20:13.358703+00:00" }
        },
        ["blocked_features"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "reason",
                ["limit"] = 50,
                ["resets_after"] = "2026-09-06T05:20:13.358703+00:00",
                ["block_reason"] = JsonNode.Parse("null"),
                ["description"] = "server supplied restriction notice"
            }
        }
    };
}
