using CodexMeter.Services;

namespace CodexMeter.Tests;

public class WebViewInitializationLifecycleTests
{
    [Fact]
    public void HostMustBeRealizedBeforeFirstNavigation()
    {
        var unrealized = WebViewInitializationCoordinator.RequireHostBeforeNavigation(false);
        Assert.False(unrealized.Success);
        Assert.Equal(WebViewInitializationFailure.HostNotRealized, unrealized.Failure);

        var host = new WebViewHostSession();
        Assert.False(host.IsRealized);
        Assert.Equal(
            WebViewInitializationFailure.HostNotRealized,
            WebViewInitializationCoordinator.RequireHostBeforeNavigation(host.IsRealized).Failure);

        host.RealizeForDiagnostic();
        Assert.True(host.IsRealized);
        Assert.True(host.IsVisible);
        Assert.True(WebViewInitializationCoordinator.RequireHostBeforeNavigation(host.IsRealized).Success);
        Assert.Throws<InvalidOperationException>(() => new WebViewHostSession().ShowLoginOnSameHost());
    }

    [Fact]
    public void HostCancel_HidesSurfaces()
    {
        var host = new WebViewHostSession();
        host.RealizeForDiagnostic();
        Assert.True(host.IsVisible);
        Assert.True(host.CheckingOverlayVisible);

        host.Cancel();
        Assert.False(host.IsVisible);
        Assert.False(host.CheckingOverlayVisible);
        Assert.False(host.LoginSurfaceVisible);
    }

    [Fact]
    public async Task InitializationTimeout_ReturnsDeterministicFailure()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var outcome = await coordinator.RunAsync(
            async ct =>
            {
                await WebViewBoundedWait.WaitAsync(Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(40), ct);
                return WebViewInitializationOutcome.Succeeded();
            },
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(WebViewInitializationFailure.Timeout, outcome.Failure);
        Assert.Equal(WebViewDiagnosticStages.InitializeTimeout, outcome.Stage);
        Assert.Equal(WebViewInitializationStatus.Failed, coordinator.Status);
    }

    [Fact]
    public async Task NavigationTimeout_ReturnsDeterministicFailure()
    {
        await Assert.ThrowsAsync<WebViewInitializationException>(() =>
            WebViewBoundedWait.WaitNavigationAsync(
                Task.Delay(Timeout.Infinite),
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None));

        var coordinator = new WebViewInitializationCoordinator();
        var outcome = await coordinator.RunAsync(
            _ => Task.FromResult(WebViewInitializationOutcome.NavigationTimedOut()),
            CancellationToken.None);

        Assert.Equal(WebViewInitializationFailure.NavigationTimeout, outcome.Failure);
        Assert.Equal(WebViewDiagnosticStages.NavigationTimeout, outcome.Stage);
        Assert.Equal(WebViewInitializationStatus.Failed, coordinator.Status);
    }

    [Fact]
    public async Task Cancellation_ReturnsWithoutCorruptingOtherWaiters()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WebViewInitializationOutcome> Stages(CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(200, ct);
            return WebViewInitializationOutcome.Succeeded();
        }

        using var cancelledWaiter = new CancellationTokenSource();
        var first = coordinator.RunAsync(Stages, cancelledWaiter.Token);
        await started.Task;
        var second = coordinator.RunAsync(Stages, CancellationToken.None);
        cancelledWaiter.Cancel();

        var cancelled = await first;
        var succeeded = await second;

        Assert.Equal(WebViewInitializationFailure.Cancelled, cancelled.Failure);
        Assert.True(succeeded.Success);
        Assert.Equal(WebViewInitializationStatus.Ready, coordinator.Status);
        Assert.Equal(1, coordinator.SharedStartCount);
        Assert.Equal(0, coordinator.WaiterCount);
    }

    [Fact]
    public async Task ConcurrentInitialization_IsSingleFlight()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        async Task<WebViewInitializationOutcome> Stages(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return WebViewInitializationOutcome.Succeeded();
        }

        var first = coordinator.RunAsync(Stages, CancellationToken.None);
        await entered.Task;
        var second = coordinator.RunAsync(Stages, CancellationToken.None);
        release.SetResult();

        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(1, runs);
        Assert.Equal(1, coordinator.SharedStartCount);
        Assert.Equal(WebViewInitializationStatus.Ready, coordinator.Status);
    }

    [Fact]
    public async Task FailedInitialization_AllowsControlledRetry()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var attempts = 0;

        Task<WebViewInitializationOutcome> Stages(CancellationToken _)
        {
            attempts++;
            return Task.FromResult(
                attempts == 1
                    ? WebViewInitializationOutcome.TimedOut()
                    : WebViewInitializationOutcome.Succeeded());
        }

        var first = await coordinator.RunAsync(Stages, CancellationToken.None);
        var second = await coordinator.RunAsync(Stages, CancellationToken.None);

        Assert.False(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, attempts);
        Assert.Equal(2, coordinator.SharedStartCount);
        Assert.Equal(WebViewInitializationStatus.Ready, coordinator.Status);
    }

    [Fact]
    public async Task ReadyCoordinator_ReusesWithoutRerunningStages()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var runs = 0;
        Task<WebViewInitializationOutcome> Stages(CancellationToken _)
        {
            runs++;
            return Task.FromResult(WebViewInitializationOutcome.Succeeded());
        }

        Assert.True((await coordinator.RunAsync(Stages, CancellationToken.None)).Success);
        Assert.True((await coordinator.RunAsync(Stages, CancellationToken.None)).Success);
        Assert.Equal(1, runs);
    }
}
