namespace ProMeter.Codex;

public sealed class CodexQuotaService
{
    public static readonly TimeSpan FlyoutRefreshAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan TaskbarRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly CodexExecutableLocator _locator;
    private readonly CodexAppServerClient _client;
    private readonly CodexSnapshotStore _store;
    private readonly string _clientVersion;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CodexQuotaSnapshot _snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);

    public CodexQuotaService(
        CodexExecutableLocator locator,
        CodexAppServerClient client,
        CodexSnapshotStore store,
        string clientVersion)
    {
        _locator = locator;
        _client = client;
        _store = store;
        _clientVersion = clientVersion;
        _snapshot = store.Load() ?? _snapshot;
    }

    public CodexQuotaSnapshot Snapshot => _snapshot;
    public bool IsRefreshing { get; private set; }

    public event Action<CodexQuotaSnapshot>? Changed;

    public static bool ShouldRefreshOnFlyoutOpen(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Status == CodexQuotaStatus.Refreshing)
        {
            return false;
        }

        var last = snapshot.LastSuccessfulRefresh ?? snapshot.LastAttemptedRefresh;
        return last is null || now - last.Value >= FlyoutRefreshAge;
    }

    public async Task<CodexRefreshResult> RefreshAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new CodexRefreshResult(_snapshot, UsedCache: true, "already-running");
        }

        IsRefreshing = true;
        Publish(_snapshot.AsRefreshing());
        var attempted = DateTimeOffset.Now;
        try
        {
            var command = _locator.Locate(configuredPath);
            if (command is null)
            {
                return PersistFailure(CodexQuotaStatus.CodexNotFound, attempted, "codex-not-found");
            }

            var session = await _client.ReadQuotaAsync(command, _clientVersion, cancellationToken).ConfigureAwait(false);
            if (session.Status == CodexQuotaStatus.Available)
            {
                var parsed = CodexRateLimitParser.Parse(session.AccountResult, session.RateLimitsResult);
                if (parsed.Status == CodexQuotaStatus.Available)
                {
                    var success = new CodexQuotaSnapshot(
                        CodexQuotaStatus.Available,
                        parsed.PlanType,
                        attempted,
                        attempted,
                        parsed.OrdinaryUsageAllowed,
                        parsed.RateLimitReachedType,
                        parsed.ResetCreditsAvailable,
                        parsed.Windows,
                        SafeDetail(session, parsed.Detail));
                    _store.Save(success);
                    Publish(success);
                    return new CodexRefreshResult(success, UsedCache: false, null);
                }

                return PersistFailure(parsed.Status, attempted, parsed.Detail ?? session.Detail);
            }

            return PersistFailure(session.Status, attempted, session.Detail);
        }
        catch (OperationCanceledException)
        {
            return PersistFailure(
                cancellationToken.IsCancellationRequested ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut,
                attempted,
                cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out");
        }
        catch (Exception ex)
        {
            return PersistFailure(CodexQuotaStatus.Unavailable, attempted, CodexProtocol.SanitizeDiagnostic(ex.GetType().Name, 80));
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
        }
    }

    private CodexRefreshResult PersistFailure(CodexQuotaStatus status, DateTimeOffset attempted, string? detail)
    {
        var cached = _store.Load() ?? _snapshot;
        CodexQuotaSnapshot next;
        if (cached.HasUsablePercentages
            && status is not CodexQuotaStatus.SignedOut and not CodexQuotaStatus.CodexNotFound)
        {
            next = cached.AsStale(attempted, detail);
        }
        else if (status is CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound)
        {
            next = new CodexQuotaSnapshot(
                status,
                cached.PlanType,
                cached.LastSuccessfulRefresh,
                attempted,
                cached.OrdinaryUsageAllowed,
                cached.RateLimitReachedType,
                cached.ResetCreditsAvailable,
                cached.HasUsablePercentages ? cached.Windows : [],
                detail);
        }
        else
        {
            next = new CodexQuotaSnapshot(
                status,
                cached.PlanType,
                cached.LastSuccessfulRefresh,
                attempted,
                null,
                null,
                cached.ResetCreditsAvailable,
                cached.HasUsablePercentages ? cached.Windows : [],
                detail);
        }

        if (next.HasUsablePercentages || next.LastSuccessfulRefresh is not null)
        {
            _store.Save(next);
        }

        Publish(next);
        return new CodexRefreshResult(next, UsedCache: cached.HasUsablePercentages, detail);
    }

    private static string? SafeDetail(CodexProtocolSession session, string? parsed)
    {
        var parts = new[] { parsed, session.Detail, session.Status == CodexQuotaStatus.Available ? null : session.Status.ToString() }
            .Where(part => !string.IsNullOrWhiteSpace(part));
        var text = string.Join(";", parts);
        return string.IsNullOrWhiteSpace(text) ? null : CodexProtocol.SanitizeDiagnostic(text, 160);
    }

    private void Publish(CodexQuotaSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(snapshot);
    }
}
