using CodexMeter.Codex;
using CodexMeter.Models;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class CodexRefreshScheduleTests
{
    [Fact]
    public void LegacySettingsRetainFiveMinutesAndHistoryInterval()
    {
        var settings = SettingsMigration.FromJson("{\"SyncIntervalMinutes\":15}");
        Assert.Equal(5, settings.CodexRefreshIntervalMinutes);
        Assert.Equal(15, settings.SyncIntervalMinutes);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)]
    [InlineData(10)] [InlineData(30)] [InlineData(60)]
    public void SelectedIntervalSurvivesRestart(int minutes)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            new SettingsStore(path).Save(new AppSettings { CodexRefreshIntervalMinutes = minutes });
            Assert.Equal(minutes, new SettingsStore(path).Load().CodexRefreshIntervalMinutes);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(3)] [InlineData(int.MaxValue)]
    public void InvalidIntervalFallsBackToDefault(int minutes)
    {
        var settings = SettingsMigration.FromJson($"{{\"CodexRefreshIntervalMinutes\":{minutes}}}");
        Assert.Equal(5, settings.CodexRefreshIntervalMinutes);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)]
    [InlineData(10)] [InlineData(30)] [InlineData(60)]
    public void AutomaticRefreshUsesSelectedCompletionBoundary(int minutes)
    {
        var completed = DateTimeOffset.Parse("2026-09-10T00:00:20Z");
        var snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with
        {
            LastSuccessfulRefresh = completed, LastAttemptedRefresh = completed.AddSeconds(-20)
        };
        var interval = TimeSpan.FromMinutes(minutes);
        Assert.False(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, completed + interval - TimeSpan.FromTicks(1), interval));
        Assert.True(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, completed + interval, interval));
        Assert.False(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot.AsRefreshing(), completed.AddHours(2), interval));
    }

    [Theory]
    [InlineData(CodexQuotaStatus.Stale)] [InlineData(CodexQuotaStatus.SignedOut)]
    [InlineData(CodexQuotaStatus.TimedOut)] [InlineData(CodexQuotaStatus.Unavailable)]
    public void OneMinuteScheduleStillProtectsFailedAttempts(CodexQuotaStatus status)
    {
        var attempt = DateTimeOffset.Parse("2026-09-10T00:00:00Z");
        var snapshot = CodexQuotaSnapshot.Empty(status) with { LastAttemptedRefresh = attempt };
        Assert.False(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, attempt.AddSeconds(119), TimeSpan.FromMinutes(1)));
        Assert.True(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, attempt.AddMinutes(2), TimeSpan.FromMinutes(1)));
    }
}