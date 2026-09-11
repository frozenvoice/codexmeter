using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;

namespace CycleArc.Tests;

public class CombinedRefreshConcurrencyTests
{
    [Fact]
    public async Task DuplicateRefreshAll_InvokesEachProviderOnce()
    {
        var chatCalls = 0;
        var codexCalls = 0;
        var release = new TaskCompletionSource();
        var coordinator = CreateGated(release, () => Interlocked.Increment(ref chatCalls), () => Interlocked.Increment(ref codexCalls));

        var first = coordinator.RefreshAllAsync(true, CancellationToken.None);
        var second = coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.Equal(1, chatCalls);
        Assert.Equal(1, codexCalls);
        Assert.True(coordinator.ManualRefreshInProgress);
        Assert.False(coordinator.RefreshButtonEnabled);
        Assert.True(coordinator.RefreshButtonActive);
        var busy = CombinedRefreshCoordinator.Present(
            coordinator.ChatGptRefreshing,
            coordinator.CodexRefreshing,
            coordinator.ManualRefreshInProgress);
        Assert.False(busy.Enabled);
        Assert.True(busy.Active);
        Assert.Equal(UiText.RefreshAllProgress, busy.ProgressText);

        release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.Equal(results[0].ChatGpt?.Status, results[1].ChatGpt?.Status);
        Assert.Equal(results[0].Codex.Snapshot.Status, results[1].Codex.Snapshot.Status);
        Assert.Equal(AppSyncStatus.UpToDate, results[0].ChatGpt?.Status);
        Assert.Equal(1, chatCalls);
        Assert.Equal(1, codexCalls);
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
    }

    [Fact]
    public async Task DuplicateRefresh_DoesNotClearOwnerBusyState()
    {
        var release = new TaskCompletionSource();
        var coordinator = CreateGated(release);

        var owner = coordinator.RefreshAllAsync(true, CancellationToken.None);
        var duplicate = coordinator.RefreshAllAsync(true, CancellationToken.None);
        await Task.Yield();
        Assert.True(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.ChatGptRefreshing);
        Assert.True(coordinator.CodexRefreshing);
        Assert.False(coordinator.RefreshButtonEnabled);
        Assert.True(coordinator.RefreshButtonActive);
        Assert.False(duplicate.IsCompleted);

        release.SetResult();
        await Task.WhenAll(owner, duplicate);
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
        Assert.False(coordinator.RefreshButtonActive);
    }

    [Fact]
    public async Task CancellingDuplicateWaiter_DoesNotCancelOwner()
    {
        var chatCalls = 0;
        var ownerSawCancel = false;
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var coordinator = new CombinedRefreshCoordinator(
            async (_, token) =>
            {
                Interlocked.Increment(ref chatCalls);
                started.TrySetResult();
                try
                {
                    await release.Task.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    ownerSawCancel = true;
                    throw;
                }

                return new SyncOutcome(AppSyncStatus.UpToDate, null, 1);
            },
            async token =>
            {
                await release.Task.WaitAsync(token);
                return SuccessCodex();
            });

        using var ownerCts = new CancellationTokenSource();
        using var waiterCts = new CancellationTokenSource();
        var owner = coordinator.RefreshAllAsync(true, ownerCts.Token);
        await started.Task;
        var waiter = coordinator.RefreshAllAsync(true, waiterCts.Token);
        waiterCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(owner.IsCompleted);
        Assert.True(coordinator.ManualRefreshInProgress);
        Assert.False(coordinator.RefreshButtonEnabled);
        Assert.True(coordinator.RefreshButtonActive);
        Assert.False(ownerSawCancel);
        Assert.Equal(1, chatCalls);

        release.SetResult();
        var result = await owner;
        Assert.Equal(AppSyncStatus.UpToDate, result.ChatGpt?.Status);
        Assert.False(ownerSawCancel);
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
    }

    [Fact]
    public async Task OwnerSuccess_ReleasesGateForLaterRefresh()
    {
        var chatCalls = 0;
        var coordinator = new CombinedRefreshCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref chatCalls);
                return Task.FromResult(new SyncOutcome(AppSyncStatus.UpToDate, null, 1));
            },
            _ => Task.FromResult(SuccessCodex()));

        await coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
        await coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.Equal(2, chatCalls);
        Assert.False(coordinator.ManualRefreshInProgress);
    }

    [Fact]
    public async Task OwnerFailure_ReleasesGateForLaterRefresh()
    {
        var chatCalls = 0;
        var coordinator = new CombinedRefreshCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref chatCalls);
                throw new InvalidOperationException("refresh-failed");
            },
            _ => Task.FromResult(SuccessCodex()));

        var failed = await coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.Equal(AppSyncStatus.Error, failed.ChatGpt?.Status);
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);

        var again = await coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.Equal(AppSyncStatus.Error, again.ChatGpt?.Status);
        Assert.Equal(2, chatCalls);
        Assert.False(coordinator.ManualRefreshInProgress);
    }

    [Fact]
    public async Task OwnerCancellation_ReleasesGateForLaterRefresh()
    {
        var chatCalls = 0;
        var block = true;
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource();
        var coordinator = new CombinedRefreshCoordinator(
            async (_, token) =>
            {
                Interlocked.Increment(ref chatCalls);
                if (block)
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }

                return new SyncOutcome(AppSyncStatus.UpToDate, null, chatCalls);
            },
            async token =>
            {
                if (block)
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

                return SuccessCodex();
            });

        var running = coordinator.RefreshAllAsync(true, cts.Token);
        await started.Task;
        Assert.True(coordinator.ManualRefreshInProgress);
        cts.Cancel();
        var cancelled = await running;
        Assert.Equal(AppSyncStatus.Idle, cancelled.ChatGpt?.Status);
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);

        block = false;
        var result = await coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.Equal(AppSyncStatus.UpToDate, result.ChatGpt?.Status);
        Assert.Equal(2, chatCalls);
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
    }

    [Fact]
    public void BothProviderBackgroundRefresh_DisablesButton()
    {
        var both = CombinedRefreshCoordinator.Present(true, true);
        Assert.False(both.Enabled);
        Assert.True(both.Active);
        Assert.Equal(UiText.RefreshAllProgress, both.ProgressText);

        var manual = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
        Assert.False(manual.Enabled);
        Assert.True(manual.Active);
    }

    [Fact]
    public void OneProviderBackgroundRefresh_MayLeaveButtonEnabled()
    {
        var chatOnly = CombinedRefreshCoordinator.Present(true, false);
        Assert.True(chatOnly.Enabled);
        Assert.True(chatOnly.Active);

        var codexOnly = CombinedRefreshCoordinator.Present(false, true);
        Assert.True(codexOnly.Enabled);
        Assert.True(codexOnly.Active);
        Assert.Equal(UiText.RefreshAllProgress, codexOnly.ProgressText);
    }

    private static CombinedRefreshCoordinator CreateGated(
        TaskCompletionSource release,
        Action? onChat = null,
        Action? onCodex = null) =>
        new(
            async (_, _) =>
            {
                onChat?.Invoke();
                await release.Task;
                return new SyncOutcome(AppSyncStatus.UpToDate, null, 1);
            },
            async _ =>
            {
                onCodex?.Invoke();
                await release.Task;
                return SuccessCodex();
            });

    private static CodexRefreshResult SuccessCodex() =>
        new(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available), false, null);
}
