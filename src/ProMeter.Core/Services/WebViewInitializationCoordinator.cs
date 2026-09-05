namespace ProMeter.Services;

public enum WebViewInitializationStatus
{
    Idle,
    Initializing,
    Ready,
    Failed
}

public enum WebViewInitializationFailure
{
    None,
    HostNotRealized,
    Timeout,
    NavigationTimeout,
    Cancelled,
    RuntimeUnavailable,
    EnvironmentFailed
}

public static class WebViewDiagnosticStages
{
    public const string InitializeStart = "initialize-start";
    public const string EnvironmentReady = "environment-ready";
    public const string CoreReady = "core-ready";
    public const string HomeNavigationStart = "home-navigation-start";
    public const string HomeNavigationComplete = "home-navigation-complete";
    public const string SessionProbe = "session-probe";
    public const string InitializeTimeout = "initialize-timeout";
    public const string NavigationTimeout = "navigation-timeout";
    public const string Cancelled = "cancelled";
    public const string RuntimeUnavailable = "runtime-unavailable";
    public const string HostInvalid = "host-invalid";
}

public static class WebViewInitializationCodes
{
    public const string Timeout = "initialize-timeout";
    public const string NavigationTimeout = "navigation-timeout";
    public const string RuntimeUnavailable = "runtime-unavailable";
    public const string HostInvalid = "host-invalid";
    public const string EnvironmentFailed = "environment-failed";
    public const string Cancelled = "cancelled";

    public static string FromFailure(WebViewInitializationFailure failure) => failure switch
    {
        WebViewInitializationFailure.Timeout => Timeout,
        WebViewInitializationFailure.NavigationTimeout => NavigationTimeout,
        WebViewInitializationFailure.RuntimeUnavailable => RuntimeUnavailable,
        WebViewInitializationFailure.HostNotRealized => HostInvalid,
        WebViewInitializationFailure.Cancelled => Cancelled,
        _ => EnvironmentFailed
    };
}

public sealed record WebViewInitializationOutcome(
    bool Success,
    WebViewInitializationFailure Failure,
    string Stage)
{
    public static WebViewInitializationOutcome Succeeded() =>
        new(true, WebViewInitializationFailure.None, WebViewDiagnosticStages.HomeNavigationComplete);

    public static WebViewInitializationOutcome Cancelled() =>
        new(false, WebViewInitializationFailure.Cancelled, WebViewDiagnosticStages.Cancelled);

    public static WebViewInitializationOutcome TimedOut() =>
        new(false, WebViewInitializationFailure.Timeout, WebViewDiagnosticStages.InitializeTimeout);

    public static WebViewInitializationOutcome NavigationTimedOut() =>
        new(false, WebViewInitializationFailure.NavigationTimeout, WebViewDiagnosticStages.NavigationTimeout);

    public static WebViewInitializationOutcome HostNotRealized() =>
        new(false, WebViewInitializationFailure.HostNotRealized, WebViewDiagnosticStages.HostInvalid);

    public static WebViewInitializationOutcome RuntimeUnavailable() =>
        new(false, WebViewInitializationFailure.RuntimeUnavailable, WebViewDiagnosticStages.RuntimeUnavailable);

    public static WebViewInitializationOutcome EnvironmentFailed() =>
        new(false, WebViewInitializationFailure.EnvironmentFailed, WebViewDiagnosticStages.InitializeStart);
}

public sealed class WebViewInitializationException : Exception
{
    public WebViewInitializationException(WebViewInitializationFailure failure, string stage)
        : base(failure.ToString())
    {
        Failure = failure;
        Stage = stage;
    }

    public WebViewInitializationFailure Failure { get; }
    public string Stage { get; }
}

public static class WebViewBoundedWait
{
    public static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(20);

    public static async Task WaitAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new WebViewInitializationException(
                WebViewInitializationFailure.Timeout,
                WebViewDiagnosticStages.InitializeTimeout);
        }
    }

    public static async Task<T> WaitAsync<T>(Task<T> task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new WebViewInitializationException(
                WebViewInitializationFailure.Timeout,
                WebViewDiagnosticStages.InitializeTimeout);
        }
    }

    public static async Task WaitNavigationAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw new WebViewInitializationException(
                WebViewInitializationFailure.NavigationTimeout,
                WebViewDiagnosticStages.NavigationTimeout);
        }
    }
}

public sealed class WebViewInitializationCoordinator
{
    private readonly object _gate = new();
    private Task<WebViewInitializationOutcome>? _inflight;
    private CancellationTokenSource? _sharedCts;

    public WebViewInitializationStatus Status { get; private set; } = WebViewInitializationStatus.Idle;
    public int SharedStartCount { get; private set; }
    public int WaiterCount { get; private set; }

    public async Task<WebViewInitializationOutcome> RunAsync(
        Func<CancellationToken, Task<WebViewInitializationOutcome>> stages,
        CancellationToken cancellationToken)
    {
        Task<WebViewInitializationOutcome> shared;
        lock (_gate)
        {
            if (Status == WebViewInitializationStatus.Ready)
            {
                return WebViewInitializationOutcome.Succeeded();
            }

            if (_inflight is { IsCompleted: false })
            {
                shared = _inflight;
                WaiterCount++;
            }
            else
            {
                Status = WebViewInitializationStatus.Initializing;
                SharedStartCount++;
                _sharedCts?.Dispose();
                _sharedCts = new CancellationTokenSource();
                WaiterCount = 1;
                var token = _sharedCts.Token;
                _inflight = ExecuteAsync(stages, token);
                shared = _inflight;
            }
        }

        try
        {
            return await shared.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return WebViewInitializationOutcome.Cancelled();
        }
        finally
        {
            lock (_gate)
            {
                WaiterCount = Math.Max(0, WaiterCount - 1);
                if (WaiterCount == 0
                    && Status == WebViewInitializationStatus.Initializing
                    && shared is { IsCompleted: false })
                {
                    try
                    {
                        _sharedCts?.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }
        }
    }

    public static WebViewInitializationOutcome RequireHostBeforeNavigation(bool hostRealized) =>
        hostRealized
            ? WebViewInitializationOutcome.Succeeded()
            : WebViewInitializationOutcome.HostNotRealized();

    private async Task<WebViewInitializationOutcome> ExecuteAsync(
        Func<CancellationToken, Task<WebViewInitializationOutcome>> stages,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await stages(cancellationToken);
            lock (_gate)
            {
                Status = outcome.Success
                    ? WebViewInitializationStatus.Ready
                    : WebViewInitializationStatus.Failed;
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                Status = WebViewInitializationStatus.Failed;
            }

            return WebViewInitializationOutcome.Cancelled();
        }
        catch (WebViewInitializationException ex)
        {
            lock (_gate)
            {
                Status = WebViewInitializationStatus.Failed;
            }

            return new WebViewInitializationOutcome(false, ex.Failure, ex.Stage);
        }
        catch
        {
            lock (_gate)
            {
                Status = WebViewInitializationStatus.Failed;
            }

            return WebViewInitializationOutcome.EnvironmentFailed();
        }
    }
}
