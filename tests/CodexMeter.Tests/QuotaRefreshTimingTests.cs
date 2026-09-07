using CodexMeter.Codex;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class QuotaRefreshTimingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-07T12:00:00Z");

    [Fact]
    public async Task SuccessfulCheckUsesCompletionTimeAndRetainsAttemptStart()
    {
        var clock = new MutableClock(Start);
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line =>
            {
                if (JsonNode.Parse(line)?["method"]?.ToString() == "account/rateLimits/read")
                    clock.UtcNow = Start.AddSeconds(25);
                return CodexScript.Standard(line);
            }
        };
        var files = new MemoryCodexFileSystem();
        files.Files.Add(@"C:\Tools\codex.exe");
        var path = Path.Combine(Path.GetTempPath(), "codexmeter-timing-" + Guid.NewGuid() + ".json");
        try
        {
            var store = new CodexSnapshotStore(path);
            var service = new CodexQuotaService(new CodexExecutableLocator(files), new CodexAppServerClient(factory),
                store, "1.0.0", clock: clock);
            var result = await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);
            Assert.Equal(CodexQuotaStatus.Available, result.Snapshot.Status);
            Assert.Equal(Start, result.Snapshot.LastAttemptedRefresh);
            Assert.Equal(Start.AddSeconds(25), result.Snapshot.LastSuccessfulRefresh);
            Assert.Equal(result.Snapshot.LastSuccessfulRefresh, store.Load()!.LastSuccessfulRefresh);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(CodexQuotaStatus.Stale)]
    [InlineData(CodexQuotaStatus.Unavailable)]
    [InlineData(CodexQuotaStatus.SignedOut)]
    [InlineData(CodexQuotaStatus.TimedOut)]
    public void RecentFailurePreventsAutomaticRetryButExpiresAtBoundary(CodexQuotaStatus status)
    {
        var snapshot = CodexQuotaSnapshot.Empty(status) with
        {
            LastSuccessfulRefresh = Start.AddMinutes(-10), LastAttemptedRefresh = Start
        };
        Assert.False(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, Start.AddSeconds(1)));
        Assert.False(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, Start.AddSeconds(119)));
        Assert.True(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, Start.AddMinutes(2)));
    }

    [Fact]
    public void SuccessfulCompletionAndColdStartUseCorrectRefreshAge()
    {
        var snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with
        {
            LastAttemptedRefresh = Start, LastSuccessfulRefresh = Start.AddSeconds(25)
        };
        Assert.False(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, Start.AddMinutes(2)));
        Assert.True(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot, Start.AddSeconds(145)));
        Assert.True(CodexQuotaService.ShouldRefreshOnFlyoutOpen(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable), Start));
        Assert.False(CodexQuotaService.ShouldRefreshOnFlyoutOpen(snapshot.AsRefreshing(), Start.AddHours(1)));
    }
}
