using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class QuotaCoverageTests
{
    [Fact]
    public void ResetAtAlone_IsNotAuthoritativeCount()
    {
        var metadata = AccountParser.ParseQuotaMetadata(new JsonObject
        {
            ["limits_progress"] = new JsonArray
            {
                new JsonObject
                {
                    ["feature_name"] = "gpt-6-pro",
                    ["reset_at"] = "2026-09-08T14:30:00Z"
                }
            }
        });

        Assert.True(metadata.MatchesGptProAllowance);
        Assert.True(metadata.Found);
        Assert.NotNull(metadata.ResetAt);
        Assert.False(metadata.IsAuthoritative);

        var coverage = new CoverageInfo { NormalChats = true };
        var snapshot = new QuotaEngine().Build(
            [],
            AppSettings.CreateDefaults(),
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            coverage,
            metadata,
            AppSyncStatus.UpToDate);

        Assert.False(snapshot.UsesServerCount);
        Assert.Equal(CoverageConfidence.Authoritative, coverage.ResetConfidence);
        Assert.NotEqual(CoverageConfidence.Authoritative, coverage.CountConfidence);
        Assert.True(snapshot.ResetEstimated is false);
        Assert.Equal(0, snapshot.Used);
        Assert.Equal(50, snapshot.Limit);
    }

    [Fact]
    public void UnrelatedProFeature_IsIgnored()
    {
        var metadata = AccountParser.ParseQuotaMetadata(new JsonObject
        {
            ["limits_progress"] = new JsonArray
            {
                new JsonObject
                {
                    ["feature_name"] = "codex_pro",
                    ["used"] = 3,
                    ["limit"] = 10,
                    ["reset_at"] = "2026-09-08T14:30:00Z"
                },
                new JsonObject
                {
                    ["feature_name"] = "plus_pro",
                    ["used"] = 9,
                    ["limit"] = 40,
                    ["reset_at"] = "2026-09-05T00:00:00Z"
                }
            }
        });

        Assert.False(metadata.MatchesGptProAllowance);
        Assert.False(metadata.IsAuthoritative);
        Assert.False(AccountParser.IsGptProAllowanceFeature("voice_pro"));
        Assert.False(AccountParser.IsGptProAllowanceFeature("pro"));
        Assert.True(AccountParser.IsGptProAllowanceFeature("gpt-5-6-pro"));
    }

    [Fact]
    public void MatchedUsedLimitReset_DisplaysServerCountAndKeepsReconstructed()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ApplyPreset(SubscriptionPreset.Pro100);
        var created = DateTimeOffset.UtcNow;
        var events = new[]
        {
            ProEvent("sol", "GPT-5.6 Sol Pro", "gpt-5-6-pro", created)
        };
        var metadata = new QuotaMetadata
        {
            Found = true,
            MatchesGptProAllowance = true,
            Used = 7,
            Limit = 50,
            ResetAt = created.AddDays(2),
            IsAuthoritative = true
        };
        var coverage = new CoverageInfo { NormalChats = true };
        var snapshot = new QuotaEngine().Build(events, settings, created, created, coverage, metadata, AppSyncStatus.UpToDate);
        Assert.True(snapshot.UsesServerCount);
        Assert.Equal(7, snapshot.Used);
        Assert.Equal(50, snapshot.Limit);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        Assert.Equal(CoverageConfidence.Authoritative, coverage.CountConfidence);
    }

    [Fact]
    public void Pro200_EvaluatesSeparateLimits()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ApplyPreset(SubscriptionPreset.Pro200);
        settings.ResetTimeZoneId = "UTC";
        var now = new DateTimeOffset(2026, 9, 4, 15, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            ProEvent("g6", "GPT-6 Pro", "gpt-6-pro", now.AddHours(-2)),
            ProEvent("sol", "GPT-5.6 Sol Pro", "gpt-5-6-pro", now.AddHours(-1))
        };
        var coverage = new CoverageInfo { NormalChats = true, ArchivedChats = true, Projects = true };
        var snapshot = new QuotaEngine().Build(events, settings, now, now, coverage, new QuotaMetadata(), AppSyncStatus.UpToDate);
        Assert.False(snapshot.UsesServerCount);
        Assert.Equal(1, snapshot.Used);
        Assert.Equal(200, snapshot.Limit);
        Assert.Equal(1, snapshot.Gpt6WeeklyUsed);
        Assert.Equal(1, snapshot.TodaySolPro);
        Assert.Equal(2, snapshot.CombinedToday);
        Assert.Equal(170, snapshot.SolProDailyLimit);
        Assert.Equal(200, snapshot.CombinedDailyLimit);
        Assert.Equal(169, snapshot.SolProDailyRemaining);
        Assert.Equal(198, snapshot.CombinedDailyRemaining);
        Assert.Equal(CoverageConfidence.HighConfidence, coverage.CountConfidence);
    }

    [Fact]
    public void PartialPage_ReducesCoverage_AndBranchesDefaultUnknown()
    {
        var coverage = new CoverageInfo
        {
            NormalChats = true,
            IndexIncomplete = true,
            FailedConversations = 1,
            ConversationIncomplete = true
        };
        Assert.False(coverage.BranchesIncluded);
        Assert.False(coverage.TemporaryChats);
        Assert.False(coverage.DeletedChats);
        Assert.Equal(CoverageConfidence.Incomplete, coverage.Confidence);
        Assert.True(coverage.ApproximatePercent < 50);
    }

    private static UsageEvent ProEvent(string id, string display, string raw, DateTimeOffset created) => new()
    {
        Id = id,
        RequestId = id,
        ConversationId = "c-" + id,
        MessageId = id,
        CreatedAt = created,
        NormalizedModel = display,
        RawModel = raw,
        Source = UsageSource.ConversationSync,
        FirstSeenAt = created,
        LastSeenAt = created,
        QuotaFamily = QuotaFamily.GptPro,
        DedupeKey = "req:" + id
    };
}
