namespace CodexMeter.Services;

public sealed record WebViewNavigationResult(
    bool Succeeded,
    WebViewInitializationFailure Failure,
    string Stage,
    string? WebErrorStatus)
{
    public static WebViewNavigationResult Success() =>
        new(true, WebViewInitializationFailure.None, WebViewDiagnosticStages.HomeNavigationComplete, null);

    public static WebViewNavigationResult Failed(string? webErrorStatus = null) =>
        new(false, WebViewInitializationFailure.NavigationFailed, WebViewDiagnosticStages.HomeNavigationFailed, SanitizeWebErrorStatus(webErrorStatus));

    public static WebViewNavigationResult TimedOut() =>
        new(false, WebViewInitializationFailure.NavigationTimeout, WebViewDiagnosticStages.NavigationTimeout, null);

    public static WebViewNavigationResult Cancelled() =>
        new(false, WebViewInitializationFailure.Cancelled, WebViewDiagnosticStages.Cancelled, null);

    public static string? SanitizeWebErrorStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return status.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-')
            ? status
            : null;
    }
}

/// <summary>
/// Tracks one in-flight WebView2 navigation so a late NavigationCompleted from an
/// expired attempt cannot complete a newer wait, and so handlers can be treated as
/// detached after success, failure, timeout, or cancellation.
/// </summary>
public sealed class WebViewNavigationWait
{
    private readonly object _gate = new();
    private int _epoch;
    private TaskCompletionSource<WebViewNavigationResult>? _pending;

    public int Epoch
    {
        get
        {
            lock (_gate)
            {
                return _epoch;
            }
        }
    }

    public bool HandlerAttached { get; private set; }

    public (int Epoch, Task<WebViewNavigationResult> Task) Begin()
    {
        lock (_gate)
        {
            _epoch++;
            if (_pending is { Task.IsCompleted: false })
            {
                _pending.TrySetCanceled();
            }

            _pending = new TaskCompletionSource<WebViewNavigationResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            HandlerAttached = true;
            return (_epoch, _pending.Task);
        }
    }

    public bool TryComplete(int epoch, bool isSuccess, string? webErrorStatus = null)
    {
        lock (_gate)
        {
            if (!HandlerAttached || epoch != _epoch || _pending is null)
            {
                return false;
            }

            HandlerAttached = false;
            var result = isSuccess
                ? WebViewNavigationResult.Success()
                : WebViewNavigationResult.Failed(webErrorStatus);
            return _pending.TrySetResult(result);
        }
    }

    public void DetachHandler()
    {
        lock (_gate)
        {
            HandlerAttached = false;
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _epoch++;
            HandlerAttached = false;
            if (_pending is { Task.IsCompleted: false })
            {
                _pending.TrySetCanceled();
            }

            _pending = null;
        }
    }

    public bool Accepts(int epoch)
    {
        lock (_gate)
        {
            return HandlerAttached && epoch == _epoch && _pending is { Task.IsCompleted: false };
        }
    }
}
