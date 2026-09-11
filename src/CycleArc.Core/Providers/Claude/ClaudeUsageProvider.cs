using CycleArc.Codex;
using CycleArc.Providers.Usage;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public sealed class ClaudeUsageProvider(CodexAccountStore accounts, IClock? clock = null, ClaudeConnectionService? connections = null) : IUsageProvider
{
    public UsageProviderId Id => UsageProviderId.Claude;
    public IUsageAccountService Create(CodexAccountProfile profile)
    {
        if (profile.Provider != Id) throw new ArgumentException("Wrong usage provider.");
        return new ClaudeQuotaService(new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profile.Id), profile.Id), clock,
            () => new ClaudeConnectionStore(accounts, profile.Id).Read(), fingerprint => connections?.Email(profile.Id, fingerprint));
    }
}

public sealed class ClaudeQuotaService : IUsageAccountService
{
    public static readonly TimeSpan FreshnessLifetime = TimeSpan.FromMinutes(5);
    private readonly ClaudeStatusLineStore _store;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClaudeStatusLineRead? _lastRead;
    private CodexQuotaSnapshot _snapshot = Empty("claude-statusline-missing");
    private CodexQuotaSnapshot? _lastPublished;
    private readonly Func<ClaudeConnectionRead>? _readConnection;
    private readonly Func<string?, string?>? _email;
    private ClaudeConnectionRead? _connection;
    private string? _lastEmail;

    public ClaudeQuotaService(ClaudeStatusLineStore store, IClock? clock = null,
        Func<ClaudeConnectionRead>? readConnection = null, Func<string?, string?>? email = null)
    {
        _store = store;
        _clock = clock ?? SystemClock.Instance;
        _readConnection = readConnection;
        _email = email;
        _connection = _readConnection?.Invoke();
        Apply(store.Read());
    }

    public CodexQuotaSnapshot Snapshot => ApplyFreshness(_snapshot, _clock.UtcNow);
    public string? Email => _connection?.Binding?.Disconnected == true ? null : _email?.Invoke(_connection?.Binding?.IdentityFingerprint);
    public string? IdentityFingerprint => Email is null ? null : _connection?.Binding?.IdentityFingerprint;
    public bool IsRefreshing { get; private set; }
    public bool ReceivesPassiveUpdates => true;
    public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => false;
    public event Action<CodexQuotaSnapshot>? Changed;

    public async Task<CodexRefreshResult> RefreshAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        IsRefreshing = true;
        try
        {
            var (read, connection) = await Task.Run(() => (_store.Read(), _readConnection?.Invoke()), token).ConfigureAwait(false);
            var changed = connection != _connection;
            _connection = connection;
            if (read != _lastRead || changed) Apply(read);
            var snapshot = Snapshot;
            PublishIfChanged(snapshot);
            return new(snapshot, snapshot.Status != CodexQuotaStatus.Available, snapshot.TechnicalDetail);
        }
        finally { IsRefreshing = false; _gate.Release(); }
    }

    private void Apply(ClaudeStatusLineRead read)
    {
        _lastRead = read;
        if (_connection?.Binding?.Disconnected == true)
        {
            _snapshot = Empty("claude-disconnected") with { Status = CodexQuotaStatus.SignedOut };
            PublishIfChanged(Snapshot);
            return;
        }
        if (_connection?.Unavailable == true)
        {
            _snapshot = Empty("claude-connection-unavailable");
            PublishIfChanged(Snapshot);
            return;
        }
        var state = read.State;
        var detail = read.Unavailable ? "claude-cache-unavailable" : state?.LastInputStatus switch
        {
            ClaudeInputStatus.Available => null,
            ClaudeInputStatus.Malformed => "claude-statusline-malformed",
            _ => "claude-statusline-missing"
        };
        if (state?.LastGood is { } good)
        {
            if (_connection?.Binding is { } binding && good.ReceivedAt < binding.ConnectedAt)
            {
                _snapshot = Empty("claude-statusline-waiting");
                PublishIfChanged(Snapshot);
                return;
            }
            var windows = new List<CodexQuotaWindow>();
            if (good.FiveHour is { } five) windows.Add(Window(five, CodexWindowKind.FiveHour, 300, "five_hour"));
            if (good.SevenDay is { } week) windows.Add(Window(week, CodexWindowKind.Weekly, 10080, "seven_day"));
            _snapshot = new CodexQuotaSnapshot(detail is null ? CodexQuotaStatus.Available : CodexQuotaStatus.Stale,
                null, good.ReceivedAt, state.LastReceivedAt, null, null, null, windows, detail)
                { Provider = UsageProviderId.Claude };
        }
        else if (_snapshot.HasUsablePercentages)
            _snapshot = _snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = detail };
        else
            _snapshot = Empty(detail) with { Status = state?.LastInputStatus == ClaudeInputStatus.Malformed
                ? CodexQuotaStatus.ProtocolMismatch : CodexQuotaStatus.Unavailable,
                LastAttemptedRefresh = state?.LastReceivedAt };
        PublishIfChanged(Snapshot);
    }

    private void PublishIfChanged(CodexQuotaSnapshot snapshot)
    {
        if (snapshot == _lastPublished && Email == _lastEmail) return;
        _lastPublished = snapshot;
        _lastEmail = Email;
        Changed?.Invoke(snapshot);
    }

    public static CodexQuotaSnapshot ApplyFreshness(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Status != CodexQuotaStatus.Available) return snapshot;
        var received = snapshot.LastSuccessfulRefresh;
        if (received is null || received > now || now - received >= FreshnessLifetime
            || snapshot.Windows.Any(window => window.ResetsAt is { } reset && reset <= now))
            return snapshot with { Status = CodexQuotaStatus.Stale, TechnicalDetail = "claude-statusline-stale" };
        return snapshot;
    }

    private static CodexQuotaSnapshot Empty(string? detail) => CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, detail)
        with { Provider = UsageProviderId.Claude };
    private static CodexQuotaWindow Window(ClaudeRateLimit value, CodexWindowKind kind, int minutes, string id) =>
        new(id, value.UsedPercentage, minutes, DateTimeOffset.FromUnixTimeSeconds(value.ResetsAt), kind);
}
