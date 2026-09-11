namespace CodexMeter.Codex;

public sealed class CodexQuotaService
{
    public static readonly TimeSpan FlyoutRefreshAge = TimeSpan.FromMinutes(2);


    private readonly CodexExecutableLocator _locator;
    private readonly CodexAppServerClient _client;
    private readonly CodexSnapshotStore _store;
    private readonly string _clientVersion;
    private readonly Action<string>? _log;
    private readonly Services.IClock _clock;
    private readonly CodexAccountProfile? _profile;
    private CodexAccountIdentity? _identity;
    private bool _discardCachedIdentity;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CodexQuotaSnapshot _snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
    private string? _lastFailureSignature;

    public CodexQuotaService(
        CodexExecutableLocator locator,
        CodexAppServerClient client,
        CodexSnapshotStore store,
        string clientVersion,
        Action<string>? log = null,
        Services.IClock? clock = null,
        CodexAccountProfile? profile = null)
    {
        _locator = locator;
        _client = client;
        _store = store;
        _clientVersion = clientVersion;
        _log = log;
        _clock = clock ?? Services.SystemClock.Instance;
        _profile = profile;
        _snapshot = store.Load() ?? _snapshot;
        if (profile is not null && _snapshot.Status is CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing)
            _snapshot = _snapshot with { Status = CodexQuotaStatus.Stale, RedeemableCredits = [] };
    }

    public CodexQuotaSnapshot Snapshot => _snapshot;
    public bool IsRefreshing { get; private set; }
    public bool IsSigningIn { get; private set; }
    public CodexAccountIdentity? Identity => _identity;

    public event Action<CodexQuotaSnapshot>? Changed;

    public static bool ShouldRefreshOnFlyoutOpen(CodexQuotaSnapshot snapshot, DateTimeOffset now, TimeSpan? refreshInterval = null)
    {
        if (snapshot.Status == CodexQuotaStatus.Refreshing)
        {
            return false;
        }

        var last = snapshot.LastSuccessfulRefresh;
        if (snapshot.LastAttemptedRefresh is { } attempt && (last is null || attempt > last)) last = attempt;
        var age = refreshInterval ?? FlyoutRefreshAge;
        // Short schedules must not turn failures into rapid automatic retries.
        if (snapshot.Status != CodexQuotaStatus.Available && age < FlyoutRefreshAge) age = FlyoutRefreshAge;
        return last is null || now - last.Value >= age;
    }

    public async Task<CodexRefreshResult> RefreshAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new CodexRefreshResult(_snapshot, UsedCache: true, "already-running");
        }

        IsRefreshing = true;
        Publish(_snapshot.AsRefreshing());
        var attempted = _clock.UtcNow;
        try
        {
            var command = Locate(configuredPath);
            if (command is null)
            {
                return PersistFailure(CodexQuotaStatus.CodexNotFound, attempted, "codex-not-found", "locate");
            }

            var session = await _client.ReadQuotaAsync(command, _clientVersion, cancellationToken).ConfigureAwait(false);
            if (_profile is not null && session.AccountResult is not null)
            {
                var identity = CodexAccountIdentity.Parse(session.AccountResult);
                if (identity.Status != CodexQuotaStatus.Available)
                {
                    if (identity.Status is CodexQuotaStatus.SignedOut or CodexQuotaStatus.Unavailable) ForgetIdentity();
                    return PersistFailure(identity.Status, attempted, "account-unavailable", "account/read");
                }
                ApplyIdentity(identity);
            }
            if (session.Status == CodexQuotaStatus.Available)
            {
                var parsed = CodexRateLimitParser.Parse(session.AccountResult, session.RateLimitsResult);
                if (parsed.Status == CodexQuotaStatus.Available)
                {
                    var success = new CodexQuotaSnapshot(
                        CodexQuotaStatus.Available,
                        parsed.PlanType,
                        _clock.UtcNow,
                        attempted,
                        parsed.OrdinaryUsageAllowed,
                        parsed.RateLimitReachedType,
                        parsed.ResetCreditsAvailable,
                        parsed.Windows,
                        SafeDetail(session, parsed.Detail),
                        parsed.ResetCreditExpirations) {
                            RedeemableCredits = CodexRateLimitParser.ReadRedeemableCredits(session.RateLimitsResult),
                            IdentityFingerprint = _identity?.Fingerprint };
                    _store.Save(success);
                    _discardCachedIdentity = false;
                    Publish(success);
                    _lastFailureSignature = null;
                    return new CodexRefreshResult(success, UsedCache: false, null);
                }

                return PersistFailure(
                    parsed.Status,
                    attempted,
                    parsed.Detail ?? session.Detail,
                    InferStage(session, parsed));
            }

            return PersistFailure(session.Status, attempted, session.Detail, InferStage(session, null));
        }
        catch (OperationCanceledException)
        {
            return PersistFailure(
                cancellationToken.IsCancellationRequested ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut,
                attempted,
                cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out",
                "client");
        }
        catch (Exception ex)
        {
            return PersistFailure(CodexQuotaStatus.Unavailable, attempted, CodexProtocol.SanitizeDiagnostic(ex.GetType().Name, 80), "client");
        }
        finally
        {
            IsRefreshing = false;
            _gate.Release();
        }
    }

    private readonly Dictionary<string, string> _redemptionKeys = new(StringComparer.Ordinal);

    // Called only after the user's explicit confirmation. Refresh and redemption share the gate.
    public async Task<CreditRedemptionOutcome> ConsumeCreditAsync(string creditId, string? configuredPath,
        CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return CreditRedemptionOutcome.Busy;
        try
        {
            if (_snapshot.Status != CodexQuotaStatus.Available
                || !_snapshot.RedeemableCredits.Any(x => x.Id == creditId))
                return CreditRedemptionOutcome.Unavailable;
            var command = Locate(configuredPath);
            if (command is null) return CreditRedemptionOutcome.Unavailable;
            if (!_redemptionKeys.TryGetValue(creditId, out var key))
                _redemptionKeys[creditId] = key = Guid.NewGuid().ToString();
            var outcome = await _client.ConsumeCreditAsync(command, _clientVersion, creditId, key, cancellationToken,
                    _profile is null ? null : _identity?.Fingerprint, requireIdentity: _profile is not null)
                .ConfigureAwait(false);
            if (outcome is CreditRedemptionOutcome.NothingToReset or CreditRedemptionOutcome.NoCredit
                or CreditRedemptionOutcome.Unavailable) _redemptionKeys.Remove(creditId);
            // Do not log IDs, protocol errors, or server response bodies.
            _log?.Invoke($"codex credit redemption outcome={outcome}");
            Publish(_snapshot with { Status = CodexQuotaStatus.Stale, RedeemableCredits = [] });
            return outcome;
        }
        finally { _gate.Release(); }
    }

    private CodexRefreshResult PersistFailure(
        CodexQuotaStatus status,
        DateTimeOffset attempted,
        string? detail,
        string stage)
    {
        var cached = _discardCachedIdentity ? _snapshot : _store.Load() ?? _snapshot;
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
                detail,
                cached.ResetCreditExpirations);
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
                detail,
                cached.ResetCreditExpirations);
        }

        next = next with { IdentityFingerprint = cached.IdentityFingerprint, RedeemableCredits = [] };

        if (_discardCachedIdentity || next.HasUsablePercentages || next.LastSuccessfulRefresh is not null)
        {
            _store.Save(next);
        }

        LogFailure(status, stage, detail);
        Publish(next);
        return new CodexRefreshResult(next, UsedCache: cached.HasUsablePercentages, detail);
    }

    private CodexLaunchCommand? Locate(string? configuredPath)
    {
        var command = _locator.Locate(configuredPath);
        return command is null || _profile is null ? command
            : command with { CodexHome = _profile.HomePath, ManagedHome = _profile.IsManaged };
    }

    private void ApplyIdentity(CodexAccountIdentity identity)
    {
        if (_snapshot.IdentityFingerprint != identity.Fingerprint || _snapshot.IdentityFingerprint is null)
            ForgetIdentity();
        _identity = identity;
    }

    private void ForgetIdentity()
    {
        _discardCachedIdentity = true;
        _identity = null;
        _redemptionKeys.Clear();
        Publish(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable));
    }

    public async Task<CodexAccountIdentity> ProbeAccountAsync(string? configuredPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var command = Locate(configuredPath);
            if (command is null) return new(CodexQuotaStatus.CodexNotFound);
            var session = await _client.ReadAccountAsync(command, _clientVersion, cancellationToken).ConfigureAwait(false);
            var identity = session.Status == CodexQuotaStatus.Available
                ? CodexAccountIdentity.Parse(session.AccountResult) : new CodexAccountIdentity(session.Status);
            if (identity.Status == CodexQuotaStatus.Available) ApplyIdentity(identity);
            return identity;
        }
        finally { _gate.Release(); }
    }

    public async Task<CodexLoginResult> LoginAsync(string? configuredPath,
        Func<Uri, CancellationToken, Task> openBrowser, CancellationToken cancellationToken)
    {
        // Reauthentication is offered only for app-owned homes. Imported sessions stay owned by their Codex installation.
        if (_profile is not { IsManaged: true }) return new(CodexQuotaStatus.Unavailable);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsSigningIn = true;
        try
        {
            ForgetIdentity();
            _store.Save(_snapshot); // An abandoned re-login must not resurrect another identity after restart.
            Directory.CreateDirectory(_profile.HomePath);
            var command = Locate(configuredPath);
            if (command is null) return new(CodexQuotaStatus.CodexNotFound);
            var result = await _client.LoginAsync(command, _clientVersion, openBrowser, cancellationToken).ConfigureAwait(false);
            if (result.Identity is { Status: CodexQuotaStatus.Available } identity) ApplyIdentity(identity);
            Publish(_snapshot with { Status = result.Status == CodexQuotaStatus.Available ? CodexQuotaStatus.Unavailable : result.Status });
            return result;
        }
        finally { IsSigningIn = false; _gate.Release(); Changed?.Invoke(_snapshot); }
    }

    private void LogFailure(CodexQuotaStatus status, string stage, string? detail)
    {
        var line = $"codex refresh status={status} stage={SanitizeStage(stage)} detail={CodexProtocol.SanitizeDiagnostic(detail, 120)}";
        if (string.Equals(line, _lastFailureSignature, StringComparison.Ordinal))
        {
            return;
        }

        _lastFailureSignature = line;
        _log?.Invoke(line);
    }

    private static string SanitizeStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return "unknown";
        }

        return stage is "initialize" or "initialized" or "account/read" or "rateLimits" or "locate" or "client"
            ? stage
            : "unknown";
    }

    private static string InferStage(CodexProtocolSession session, CodexParseResult? parsed)
    {
        if (session.Status is CodexQuotaStatus.ProtocolMismatch or CodexQuotaStatus.TimedOut or CodexQuotaStatus.Unavailable
            && !session.SentMethods.Contains("initialized"))
        {
            return "initialize";
        }

        if (session.SentMethods.Contains("account/read")
            && !session.SentMethods.Contains("account/rateLimits/read"))
        {
            return "account/read";
        }

        if (parsed?.Status == CodexQuotaStatus.ProtocolMismatch)
        {
            if (session.AccountResult is JsonObject account && account.ContainsKey("error"))
            {
                return "account/read";
            }

            return "rateLimits";
        }

        return session.SentMethods.Contains("account/rateLimits/read") ? "rateLimits" : "account/read";
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
