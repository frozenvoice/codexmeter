namespace CycleArc.Codex;

/// <summary>Shares one refresh across tray, flyout, timer and widget callers.</summary>
public sealed class CodexRefreshCoordinator
{
    private readonly Func<CancellationToken, Task<CodexRefreshResult>> _refresh;
    private readonly object _gate = new();
    private Task<CodexRefreshResult>? _active;
    public CodexRefreshCoordinator(Func<CancellationToken, Task<CodexRefreshResult>> refresh) => _refresh = refresh;
    public bool IsRefreshing { get { lock (_gate) return _active is { IsCompleted: false }; } }
    public event Action? StateChanged;

    public Task<CodexRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<CodexRefreshResult> owner;
        lock (_gate)
        {
            if (_active is { IsCompleted: false } active) return active.WaitAsync(cancellationToken);
            owner = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _active = owner.Task;
        }
        StateChanged?.Invoke();
        _ = RunOwnedAsync(owner, cancellationToken);
        return owner.Task;
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate) return _active ?? Task.CompletedTask;
    }

    private async Task RunOwnedAsync(TaskCompletionSource<CodexRefreshResult> owner, CancellationToken token)
    {
        CodexRefreshResult? result = null;
        Exception? error = null;
        try { result = await _refresh(token).ConfigureAwait(false); }
        catch (Exception ex) { error = ex; }
        lock (_gate)
        {
            if (error is OperationCanceledException) owner.TrySetCanceled(token);
            else if (error is not null) owner.TrySetException(error);
            else owner.TrySetResult(result!);
            if (ReferenceEquals(_active, owner.Task)) _active = null;
        }
        StateChanged?.Invoke();
    }
}
