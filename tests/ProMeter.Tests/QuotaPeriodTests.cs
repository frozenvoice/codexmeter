using ProMeter.Services;

namespace ProMeter.Tests;

public class QuotaPeriodTests
{
    [Fact]
    public void Period_UsesResetAnchor()
    {
        var settings = new AppSettings
        {
            ResetWeekday = DayOfWeek.Monday,
            ResetTime = new TimeSpan(14, 30, 0),
            ResetTimeZoneId = "UTC"
        };
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var (start, end) = QuotaPeriodCalculator.CurrentPeriod(settings, now);
        Assert.True(now >= start && now < end);
        Assert.Equal(TimeSpan.FromDays(7), end - start);
        Assert.Equal(DayOfWeek.Monday, end.UtcDateTime.DayOfWeek);
        Assert.Equal(14, end.UtcDateTime.Hour);
        Assert.Equal(30, end.UtcDateTime.Minute);
    }

    [Fact]
    public void ServerReset_WinsOverSettings()
    {
        var settings = AppSettings.CreateDefaults();
        var reset = new DateTimeOffset(2026, 9, 8, 14, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
        var (start, end) = QuotaPeriodCalculator.CurrentPeriod(settings, now, reset);
        Assert.Equal(reset.AddDays(-7), start);
        Assert.Equal(reset, end);
    }

    [Fact]
    public void Boundary_IsHalfOpen()
    {
        var start = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(7);
        Assert.True(QuotaPeriodCalculator.InRange(start, start, end));
        Assert.False(QuotaPeriodCalculator.InRange(end, start, end));
    }
}
