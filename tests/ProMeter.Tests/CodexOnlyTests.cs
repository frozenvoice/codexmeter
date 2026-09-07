using ProMeter.Codex;
using ProMeter.Services;

namespace ProMeter.Tests;

public class CodexOnlyTests
{
    private static CodexQuotaSnapshot Snapshot(CodexQuotaStatus status, double? used = 25) =>
        new(status, "pro", DateTimeOffset.Parse("2026-09-07T05:00:00Z"), null, null, null, 2,
            [new CodexQuotaWindow("codex", used, 10080, DateTimeOffset.Parse("2026-09-14T05:00:00Z"), CodexWindowKind.Weekly)], null);

    [Theory]
    [InlineData(CodexQuotaStatus.SignedOut)]
    [InlineData(CodexQuotaStatus.CodexNotFound)]
    [InlineData(CodexQuotaStatus.Unavailable)]
    public void UnavailableIdentityDoesNotExposeSavedPercentage(CodexQuotaStatus status)
    {
        var snapshot = Snapshot(status);
        Assert.False(CodexRingPresentation.From(snapshot).IsAvailable);
        Assert.DoesNotContain("25", CodexMeterPresentation.CompactText(snapshot));
        Assert.DoesNotContain("25%", CodexMeterPresentation.Tooltip(snapshot));
    }

    [Theory]
    [InlineData(null, false, "?")]
    [InlineData(0d, true, "0%")]
    [InlineData(100d, true, "100%")]
    public void UnknownIsDistinctFromZero(double? used, bool available, string center)
    {
        var ring = CodexRingPresentation.From(Snapshot(CodexQuotaStatus.Available, used));
        Assert.Equal(available, ring.IsAvailable);
        Assert.Equal(center, ring.CenterValueText);
    }

    [Fact]
    public void CachedUsageIsExplicitlyMarkedStale()
    {
        var snapshot = Snapshot(CodexQuotaStatus.Stale);
        Assert.Contains("25%", CodexMeterPresentation.CompactText(snapshot));
        Assert.Contains("~", CodexMeterPresentation.CompactText(snapshot));
        Assert.Contains(CodexMeterPresentation.StatusLabel(snapshot), CodexMeterPresentation.Tooltip(snapshot));
    }

    [Fact]
    public async Task OverlappingRefreshesShareWorkAndKeepBusyUntilOwnerCompletes()
    {
        var source = new TaskCompletionSource<CodexRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var coordinator = new CodexRefreshCoordinator(_ => { Interlocked.Increment(ref calls); return source.Task; });
        var first = coordinator.RefreshAsync(CancellationToken.None);
        var second = coordinator.RefreshAsync(CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.True(coordinator.IsRefreshing);
        Assert.False(second.IsCompleted);
        source.SetResult(new CodexRefreshResult(Snapshot(CodexQuotaStatus.Available), false, null));
        var values = await Task.WhenAll(first, second);
        Assert.Equal(values[0], values[1]);
        Assert.False(coordinator.IsRefreshing);
        await coordinator.WaitForIdleAsync();
    }

    [Fact]
    public async Task CancellingWaiterDoesNotCancelOwnerOrEnableRefreshEarly()
    {
        var source = new TaskCompletionSource<CodexRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new CodexRefreshCoordinator(_ => source.Task);
        var first = coordinator.RefreshAsync(CancellationToken.None);
        using var waiter = new CancellationTokenSource();
        var second = coordinator.RefreshAsync(waiter.Token);
        waiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.True(coordinator.IsRefreshing);
        Assert.False(first.IsCompleted);
        source.SetResult(new CodexRefreshResult(Snapshot(CodexQuotaStatus.Available), false, null));
        await first;
        Assert.False(coordinator.IsRefreshing);
    }

    [Fact]
    public async Task FailedRefreshReleasesSlotForRetry()
    {
        var attempts = 0;
        var coordinator = new CodexRefreshCoordinator(_ => ++attempts == 1
            ? Task.FromException<CodexRefreshResult>(new InvalidOperationException("synthetic"))
            : Task.FromResult(new CodexRefreshResult(Snapshot(CodexQuotaStatus.Available), false, null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RefreshAsync(CancellationToken.None));
        Assert.False(coordinator.IsRefreshing);
        await coordinator.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, attempts);
    }
}