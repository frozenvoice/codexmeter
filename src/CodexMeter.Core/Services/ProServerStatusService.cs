namespace CodexMeter.Services;

public readonly record struct ProServerStatusRefreshResult(
    ProServerStatus Status,
    bool UsedCache,
    bool TransientFailure,
    string? Detail);

public sealed class ProServerStatusService
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FlyoutStaleAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ResetRecheckDelay = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ResetRecheckMin = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ResetRecheckMax = TimeSpan.FromSeconds(15);

    private readonly ProServerStatusStore _store;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private ProServerStatus _status = ProServerStatus.Unknown();
    private Task<ProServerStatusRefreshResult>? _active;

    public ProServerStatusService(ProServerStatusStore? store = null, IClock? clock = null)
    {
        _store = store ?? new ProServerStatusStore();
        _clock = clock ?? SystemClock.Instance;
        _status = _store.Load() ?? _status;
    }

    public ProServerStatus Current => _status;
    public bool IsRefreshing { get; private set; }
    public event Action<ProServerStatus>? Changed;

    public bool RecoverLastConfirmedFromSettings(AppSettings settings)
    {
        if (!ProServerStatus.TryRecoverLastConfirmedFromRestrictionKey(_status, settings.LastNotifiedProRestrictionKey))
        {
            return false;
        }

        _store.Save(_status);
        Changed?.Invoke(_status);
        return true;
    }

    public static bool ShouldRefreshOnFlyoutOpen(ProServerStatus status, DateTimeOffset now)
    {
        if (status.RestrictionState == ProRestrictionState.Unknown && !status.ServerObserved)
        {
            return true;
        }

        var last = status.LastSuccessfulRefresh ?? status.LastRefreshAttempt;
        return last is null || now - last.Value >= FlyoutStaleAge || status.Stale;
    }

    public static DateTimeOffset? NextResetRecheck(ProServerStatus status, DateTimeOffset now)
    {
        if (status.ResetAt is not { } reset || reset <= now)
        {
            return null;
        }

        var due = reset + ResetRecheckDelay;
        if (due < reset + ResetRecheckMin)
        {
            due = reset + ResetRecheckMin;
        }

        if (due > reset + ResetRecheckMax)
        {
            due = reset + ResetRecheckMax;
        }

        return due;
    }

    public static bool ShouldPeriodicRefresh(AppSettings settings) =>
        settings.TaskbarStatusEnabled || settings.FloatingWidgetEnabled;

    public void ApplyFromMetadata(QuotaMetadataSet? metadata, DateTimeOffset? now = null)
    {
        if (metadata?.ProServerStatus is not { ServerObserved: true } observed)
        {
            return;
        }

        observed.LastSuccessfulRefresh = now ?? _clock.UtcNow;
        observed.LastRefreshAttempt = observed.LastSuccessfulRefresh;
        observed.Stale = false;
        Persist(observed);
    }

    public Task<ProServerStatusRefreshResult> RefreshAsync(
        IChatGptProvider provider,
        CancellationToken cancellationToken = default)
    {
        Task<ProServerStatusRefreshResult> shared;
        lock (_gate)
        {
            if (_active is { IsCompleted: false } running)
            {
                return WaitForSharedAsync(running, cancellationToken);
            }

            shared = RunOwnedAsync(provider, cancellationToken);
            _active = shared;
        }

        return shared;
    }

    private async Task<ProServerStatusRefreshResult> RunOwnedAsync(
        IChatGptProvider provider,
        CancellationToken cancellationToken)
    {
        IsRefreshing = true;
        var attempted = _clock.UtcNow;
        try
        {
            var metadata = await provider.TryGetQuotaMetadataAsync(cancellationToken).ConfigureAwait(false);
            var parsed = metadata.ProServerStatus;
            if (!parsed.ServerObserved)
            {
                return RetainAfterFailure(attempted, "empty-status");
            }

            parsed.LastSuccessfulRefresh = attempted;
            parsed.LastRefreshAttempt = attempted;
            parsed.Stale = false;
            Persist(parsed);
            return new ProServerStatusRefreshResult(parsed, UsedCache: false, TransientFailure: false, null);
        }
        catch (OperationCanceledException)
        {
            _status.LastRefreshAttempt = attempted;
            throw;
        }
        catch (ChatGptProviderException ex) when (ex.IsFatalTransportFailure)
        {
            return RetainAfterFailure(attempted, AppLog.Sanitize(ex.Message));
        }
        catch (Exception ex)
        {
            return RetainAfterFailure(attempted, AppLog.Sanitize(ex.GetType().Name));
        }
        finally
        {
            lock (_gate)
            {
                _active = null;
            }

            IsRefreshing = false;
        }
    }

    private ProServerStatusRefreshResult RetainAfterFailure(DateTimeOffset attempted, string detail)
    {
        if (_status.ServerObserved)
        {
            var stale = _status.AsStale();
            stale.LastRefreshAttempt = attempted;
            Persist(stale);
            return new ProServerStatusRefreshResult(stale, UsedCache: true, TransientFailure: true, detail);
        }

        _status.LastRefreshAttempt = attempted;
        Changed?.Invoke(_status);
        return new ProServerStatusRefreshResult(_status, UsedCache: true, TransientFailure: true, detail);
    }

    private void Persist(ProServerStatus status)
    {
        ProServerStatus.RetainConfirmedReset(status, _status);
        _status = status;
        _store.Save(status);
        Changed?.Invoke(status);
    }

    private static async Task<ProServerStatusRefreshResult> WaitForSharedAsync(
        Task<ProServerStatusRefreshResult> shared,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return await shared.ConfigureAwait(false);
        }

        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            canceled);
        var completed = await Task.WhenAny(shared, canceled.Task).ConfigureAwait(false);
        if (completed != shared)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return await shared.ConfigureAwait(false);
    }
}
