using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class PeriodUsageCountTests
{
    [Fact]
    public void CurrentPeriodConversation_MayContainOlderEvents_WeeklyCountUsesCreatedAt()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetWeekday = DayOfWeek.Monday;
        settings.ResetTime = TimeSpan.Zero;
        settings.ResetTimeZoneId = "UTC";
        var now = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var (periodStart, periodEnd) = QuotaPeriodCalculator.CurrentPeriod(settings, now);
        var oldTime = periodStart.AddDays(-10).ToUnixTimeSeconds();
        var newTime = now.AddHours(-2).ToUnixTimeSeconds();
        var models = new ModelNormalizer();
        var parser = new ConversationParser(models);
        var parsed = parser.Parse(
            ConversationFixtures.TwoProTurns("conv-mixed", oldTime, newTime),
            new ConversationParseContext
            {
                ConversationId = "conv-mixed",
                UpdateTime = newTime
            });

        Assert.Equal(2, parsed.Events.Count);
        Assert.All(parsed.Events, e => Assert.Equal(QuotaFamily.GptPro, e.QuotaFamily));
        Assert.Contains(parsed.Events, e => !QuotaPeriodCalculator.InRange(e.CreatedAt, periodStart, periodEnd));
        Assert.Contains(parsed.Events, e => QuotaPeriodCalculator.InRange(e.CreatedAt, periodStart, periodEnd));

        var snapshot = new QuotaEngine().Build(
            parsed.Events,
            settings,
            now,
            now,
            new CoverageInfo { NormalChats = true, NormalIndexState = CollectionState.Complete },
            new QuotaMetadataSet(),
            AppSyncStatus.UpToDate);

        Assert.Equal(1, snapshot.Used);
        Assert.Equal(50, snapshot.Limit);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        Assert.Equal(2, parsed.Events.Count);
    }
}
