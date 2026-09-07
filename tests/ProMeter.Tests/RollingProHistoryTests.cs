using ProMeter.Services;

namespace ProMeter.Tests;

public class RollingProHistoryTests
{
    private static readonly DateTimeOffset Monday = new(2026, 9, 7, 4, 38, 0, TimeSpan.Zero);

    [Fact]
    public void UnconfiguredMonday_DoesNotEraseWeekendProHistory()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "Korea Standard Time";
        var events = new[]
        {
            Pro("sunday", Monday.AddDays(-1)), Pro("friday", Monday.AddDays(-3)), Pro("as-of", Monday),
            Pro("too-old", Monday.AddDays(-8)), Pro("future", Monday.AddDays(1))
        };
        var snapshot = new QuotaEngine().Build(events, settings, Monday, Monday,
            new CoverageInfo { NormalChats = true }, new QuotaMetadataSet(), AppSyncStatus.UpToDate);
        Assert.Equal(3, snapshot.ReconstructedUsed);
        Assert.Equal(Monday.AddDays(-7).AddTicks(1), snapshot.PeriodStart);
        Assert.False(snapshot.CurrentCycleKnown);
        Assert.Null(snapshot.ResetAt);
        Assert.False(snapshot.UsesServerWeeklyCount);
        Assert.False(ProStatusPresentation.From(snapshot).ExactRemainingAvailable);
        Assert.Equal(UiText.EstimatedPeriodReconstructed, ProStatusPresentation.From(snapshot).ReconstructedLabel);
        Assert.Equal(UiText.NotConfirmed, DisplayFormatting.ResetDisplay(snapshot).TimeValue);
    }

    [Fact]
    public void UnknownAnchor_DoesNotDependOnCalendarWeekPreference()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetWeekday = DayOfWeek.Thursday;
        var period = ProQuotaPeriodResolver.Resolve(settings, Monday, ProServerStatus.Unknown());
        Assert.Equal(Monday.AddDays(-7).AddTicks(1), period.Start);
        Assert.Equal(Monday.AddTicks(1), period.End);
        Assert.Equal(TimeSpan.FromDays(7), period.End - period.Start);
        Assert.False(period.CurrentCycleKnown);
    }

    [Fact]
    public void ConfirmedUserAnchor_RemainsCalendarBased()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetAnchorConfigured = true;
        settings.ResetTimeZoneId = "Korea Standard Time";
        var period = ProQuotaPeriodResolver.Resolve(settings, Monday, ProServerStatus.Unknown());
        Assert.Equal(new DateTimeOffset(2026, 9, 6, 15, 0, 0, TimeSpan.Zero), period.Start);
        Assert.True(period.CurrentCycleKnown);
        Assert.Equal(ResetAnchorSource.UserConfigured, period.Source);
    }

    private static UsageEvent Pro(string id, DateTimeOffset at) => new()
    {
        Id = id, ConversationId = id, CreatedAt = at,
        QuotaFamily = QuotaFamily.GptPro, RawModel = "gpt-6-pro", NormalizedModel = "GPT-6 Pro"
    };
}
