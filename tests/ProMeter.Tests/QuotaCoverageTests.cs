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
                    ["feature_name"] = "gpt_pro_weekly",
                    ["reset_at"] = "2026-09-08T14:30:00Z"
                }
            }
        });

        Assert.True(metadata.MatchesGptProAllowance);
        Assert.True(metadata.Found);
        Assert.NotNull(metadata.SharedProWeekly?.ResetAt);
        Assert.False(metadata.SharedProWeekly!.IsAuthoritative);

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
        Assert.Equal(ResetAnchorSource.Server, coverage.ResetAnchorSource);
        Assert.True(snapshot.ResetEstimated is false);
        Assert.Equal(0, snapshot.Used);
        Assert.Equal(50, snapshot.Limit);
    }

    [Fact]
    public void DailyReset_IsNotUsedForSevenDayPeriod()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ApplyPreset(SubscriptionPreset.Pro200);
        settings.ResetTimeZoneId = "UTC";
        settings.ResetWeekday = DayOfWeek.Monday;
        settings.ResetTime = TimeSpan.Zero;
        var now = new DateTimeOffset(2026, 9, 4, 15, 0, 0, TimeSpan.Zero);
        var dailyReset = now.AddHours(6);
        var metadata = new QuotaMetadataSet
        {
            SolProDaily = new QuotaWindow
            {
                Found = true,
                Used = 10,
                Limit = 170,
                ResetAt = dailyReset,
                FeatureName = "gpt-5-6-pro-daily"
            }
        };
        var snapshot = new QuotaEngine().Build([], settings, now, now, new CoverageInfo(), metadata, AppSyncStatus.UpToDate);
        Assert.NotEqual(dailyReset, snapshot.ResetAt);
        Assert.True(snapshot.PeriodEnd - snapshot.PeriodStart == TimeSpan.FromDays(7));
        Assert.True(snapshot.ResetEstimated);
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
        Assert.False(metadata.Found);
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
        var metadata = new QuotaMetadataSet
        {
            SharedProWeekly = new QuotaWindow
            {
                Found = true,
                Used = 7,
                Limit = 50,
                ResetAt = created.AddDays(2),
                FeatureName = "gpt_pro_weekly"
            }
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
        var snapshot = new QuotaEngine().Build(events, settings, now, now, coverage, new QuotaMetadataSet(), AppSyncStatus.UpToDate);
        Assert.False(snapshot.UsesServerCount);
        Assert.False(snapshot.UsesServerWeeklyCount);
        Assert.False(snapshot.UsesServerSolDailyCount);
        Assert.False(snapshot.UsesServerCombinedDailyCount);
        Assert.Equal(1, snapshot.Used);
        Assert.Equal(200, snapshot.Limit);
        Assert.Equal(1, snapshot.Gpt6WeeklyUsed);
        Assert.Equal(1, snapshot.TodaySolPro);
        Assert.Equal(2, snapshot.CombinedToday);
        Assert.Equal(170, snapshot.SolProDailyLimit);
        Assert.Equal(200, snapshot.CombinedDailyLimit);
        Assert.Equal(169, snapshot.SolProDailyRemaining);
        Assert.Equal(198, snapshot.CombinedDailyRemaining);
        Assert.Equal(CoverageConfidence.Estimated, coverage.CountConfidence);
        Assert.Equal(ResetAnchorSource.Default, coverage.ResetAnchorSource);
        Assert.Equal(ResetAnchorSource.Default, snapshot.ResetAnchorSource);
    }

    [Fact]
    public void Pro200Windows_AreOrderIndependent()
    {
        var entries = new JsonNode[]
        {
            new JsonObject { ["feature_name"] = "gpt-6-pro-weekly", ["used"] = 11, ["limit"] = 200, ["reset_at"] = "2026-09-08T00:00:00Z" },
            new JsonObject { ["feature_name"] = "gpt-5-6-pro-daily", ["used"] = 4, ["limit"] = 170, ["reset_at"] = "2026-09-05T00:00:00Z" },
            new JsonObject { ["feature_name"] = "combined_pro_daily", ["used"] = 9, ["limit"] = 200, ["reset_at"] = "2026-09-05T00:00:00Z" }
        };

        foreach (var order in new[] { (0, 1, 2), (2, 0, 1), (1, 2, 0) })
        {
            var parsed = AccountParser.ParseQuotaMetadata(new JsonObject
            {
                ["limits_progress"] = new JsonArray
                {
                    entries[order.Item1]!.DeepClone(),
                    entries[order.Item2]!.DeepClone(),
                    entries[order.Item3]!.DeepClone()
                }
            });
            Assert.Equal(11, parsed.Gpt6ProWeekly?.Used);
            Assert.Equal(200, parsed.Gpt6ProWeekly?.Limit);
            Assert.Equal(4, parsed.SolProDaily?.Used);
            Assert.Equal(170, parsed.SolProDaily?.Limit);
            Assert.Equal(9, parsed.CombinedProDaily?.Used);
            Assert.Equal(200, parsed.CombinedProDaily?.Limit);
            Assert.Null(parsed.SharedProWeekly);
            Assert.Equal(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero), parsed.WeeklyResetAt);

            var settings = AppSettings.CreateDefaults();
            settings.ApplyPreset(SubscriptionPreset.Pro200);
            settings.ResetTimeZoneId = "UTC";
            var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
            var snapshot = new QuotaEngine().Build([], settings, now, now, new CoverageInfo { NormalChats = true }, parsed, AppSyncStatus.UpToDate);
            Assert.True(snapshot.UsesServerCount);
            Assert.True(snapshot.UsesServerWeeklyCount);
            Assert.True(snapshot.UsesServerSolDailyCount);
            Assert.True(snapshot.UsesServerCombinedDailyCount);
            Assert.Equal(11, snapshot.Used);
            Assert.Equal(200, snapshot.Limit);
            Assert.Equal(4, snapshot.TodaySolPro);
            Assert.Equal(9, snapshot.CombinedToday);
            Assert.Equal(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero), snapshot.ResetAt);
        }
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

    [Fact]
    public void PerfectHistory_UntouchedDefaultReset_IsEstimated()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        var coverage = new CoverageInfo { NormalChats = true, ArchivedChats = true, Projects = true };
        var snapshot = new QuotaEngine().Build([], settings, new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, coverage, new QuotaMetadataSet(), AppSyncStatus.UpToDate);
        Assert.Equal(CoverageConfidence.Estimated, coverage.CountConfidence);
        Assert.Equal(CoverageConfidence.Estimated, coverage.ResetConfidence);
        Assert.Equal(ResetAnchorSource.Default, snapshot.ResetAnchorSource);
        Assert.True(snapshot.ResetEstimated);
        Assert.False(settings.ResetAnchorConfigured);
    }

    [Fact]
    public void PerfectHistory_UserConfiguredReset_IsHighConfidence()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.ResetAnchorConfigured = true;
        var coverage = new CoverageInfo { NormalChats = true, ArchivedChats = true, Projects = true };
        var snapshot = new QuotaEngine().Build([], settings, new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow, coverage, new QuotaMetadataSet(), AppSyncStatus.UpToDate);
        Assert.Equal(CoverageConfidence.HighConfidence, coverage.CountConfidence);
        Assert.Equal(CoverageConfidence.HighConfidence, coverage.ResetConfidence);
        Assert.Equal(ResetAnchorSource.UserConfigured, snapshot.ResetAnchorSource);
        Assert.True(snapshot.ResetEstimated);
        Assert.NotEqual(CoverageConfidence.Authoritative, coverage.CountConfidence);
    }

    [Fact]
    public void ServerUsedLimitReset_IsAuthoritative()
    {
        var settings = AppSettings.CreateDefaults();
        var created = DateTimeOffset.UtcNow;
        var metadata = new QuotaMetadataSet
        {
            SharedProWeekly = new QuotaWindow
            {
                Found = true,
                Used = 7,
                Limit = 50,
                ResetAt = created.AddDays(2),
                FeatureName = "gpt_pro_weekly"
            }
        };
        var coverage = new CoverageInfo { NormalChats = true };
        var snapshot = new QuotaEngine().Build([], settings, created, created, coverage, metadata, AppSyncStatus.UpToDate);
        Assert.Equal(CoverageConfidence.Authoritative, coverage.CountConfidence);
        Assert.Equal(CoverageConfidence.Authoritative, coverage.ResetConfidence);
        Assert.Equal(ResetAnchorSource.Server, snapshot.ResetAnchorSource);
        Assert.True(snapshot.UsesServerCount);
        Assert.Equal(7, snapshot.Used);
    }

    [Fact]
    public void AmbiguousGpt56Pro_IsDiagnosticUnlessCadenceIsExplicit()
    {
        Assert.Equal(QuotaWindowKind.SolProDaily, AccountParser.ClassifyGptProWindow("gpt-5-6-pro-daily"));
        Assert.Equal(QuotaWindowKind.Unclassified, AccountParser.ClassifyGptProWindow("gpt-5-6-pro"));
        Assert.Equal(QuotaWindowKind.SharedProWeekly, AccountParser.ClassifyGptProWindow("gpt_pro_weekly"));
        Assert.Equal(QuotaWindowKind.Gpt6ProWeekly, AccountParser.ClassifyGptProWindow("gpt-6-pro-weekly"));
        Assert.Equal(QuotaWindowKind.Unclassified, AccountParser.ClassifyGptProWindow("gpt-6-pro"));

        var parsed = AccountParser.ParseQuotaMetadata(new JsonObject
        {
            ["limits_progress"] = new JsonArray
            {
                new JsonObject { ["feature_name"] = "gpt-5-6-pro", ["used"] = 12, ["limit"] = 170, ["reset_at"] = "2026-09-05T00:00:00Z" }
            }
        });
        Assert.Null(parsed.SolProDaily);
        Assert.Null(parsed.SharedProWeekly);
        Assert.Contains(parsed.Diagnostics, window => window.FeatureName == "gpt-5-6-pro");
        Assert.True(parsed.Found);
        Assert.False(parsed.MatchesGptProAllowance);
    }

    [Fact]
    public void Pro100_DoesNotExposeDailyLimitsFromAmbiguousServerMetadata()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ApplyPreset(SubscriptionPreset.Pro100);
        var created = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var metadata = AccountParser.ParseQuotaMetadata(new JsonObject
        {
            ["limits_progress"] = new JsonArray
            {
                new JsonObject { ["feature_name"] = "gpt-5-6-pro", ["used"] = 12, ["limit"] = 170, ["reset_at"] = "2026-09-05T00:00:00Z" }
            }
        });
        var events = new[] { ProEvent("sol", "GPT-5.6 Sol Pro", "gpt-5-6-pro", created) };
        var snapshot = new QuotaEngine().Build(events, settings, created, created, new CoverageInfo { NormalChats = true }, metadata, AppSyncStatus.UpToDate);
        Assert.Null(snapshot.SolProDailyLimit);
        Assert.Null(snapshot.CombinedDailyLimit);
        Assert.False(snapshot.UsesServerCount);
        Assert.Equal(1, snapshot.Used);
        Assert.Equal(50, snapshot.Limit);
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
