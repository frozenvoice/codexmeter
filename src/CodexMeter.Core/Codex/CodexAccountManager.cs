namespace CodexMeter.Codex;

public sealed record CodexDiscoveryResult(int Added, int Failed, int SignedOut);

/// <summary>Owns an extensible collection of isolated services and one shared refresh.</summary>
public sealed class CodexAccountManager
{
    private readonly object _gate = new();
    private readonly CodexAccountStore _store;
    private readonly Func<CodexAccountProfile, CodexQuotaService> _createService;
    private readonly Func<string?> _configuredPath;
    private readonly Dictionary<string, CodexQuotaService> _services = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _processSlots = new(2, 2);
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private CodexAccountConfiguration _configuration;
    private string? _loginProfile;

    public CodexAccountManager(CodexAccountStore store, string defaultHome,
        Func<CodexAccountProfile, CodexQuotaService> createService, Func<string?> configuredPath)
    {
        _store = store;
        _configuration = store.LoadOrMigrate(defaultHome);
        _createService = createService;
        _configuredPath = configuredPath;
        foreach (var profile in _configuration.Profiles) AddService(profile);
        Refresh = new CodexRefreshCoordinator(RefreshAllAsync);
        Refresh.StateChanged += () => Changed?.Invoke();
    }

    public event Action? Changed;
    public CodexRefreshCoordinator Refresh { get; }
    public string SelectedId { get { lock (_gate) return _configuration.SelectedId; } }
    public bool IsSigningIn { get { lock (_gate) return _loginProfile is not null; } }
    public IReadOnlyList<CodexAccountView> Accounts
    {
        get
        {
            lock (_gate)
            {
                var repeated = _services.Values.Select(s => s.Identity?.Fingerprint).OfType<string>()
                    .GroupBy(value => value, StringComparer.Ordinal).Where(group => group.Count() > 1)
                    .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
                return _configuration.Profiles.Select(profile =>
                {
                    var service = _services[profile.Id];
                    return new CodexAccountView(profile, service.Snapshot, service.Identity?.Email, _loginProfile == profile.Id,
                        service.Identity?.Fingerprint is { } fingerprint && repeated.Contains(fingerprint));
                }).ToArray();
            }
        }
    }
    public CodexAccountView? Selected => Accounts.FirstOrDefault(a => a.Profile.Id == SelectedId);
    public CodexQuotaSnapshot Snapshot => Selected?.Snapshot ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut);

    public void Select(string id)
    {
        lock (_gate)
        {
            if (!_services.ContainsKey(id) || id == _configuration.SelectedId) return;
            Save(_configuration with { SelectedId = id });
        }
        Changed?.Invoke();
    }

    public void Rename(string id, string label)
    {
        lock (_gate) Save(_configuration with { Profiles = _configuration.Profiles.Select(p =>
            p.Id == id ? p with { Label = CodexAccountStore.CleanLabel(label) } : p).ToArray() });
        Changed?.Invoke();
    }

    public bool Move(string id, int direction)
    {
        if (direction is not (-1 or 1)) return false;
        lock (_gate)
        {
            var profiles = _configuration.Profiles.ToArray();
            var index = Array.FindIndex(profiles, profile => profile.Id == id);
            var destination = index + direction;
            if (index < 0 || destination < 0 || destination >= profiles.Length) return false;
            (profiles[index], profiles[destination]) = (profiles[destination], profiles[index]);
            // Ordering is presentation only; selected identity, services, homes and caches stay bound to their IDs.
            Save(_configuration with { Profiles = profiles });
        }
        Changed?.Invoke();
        return true;
    }

    // Forget only local references. Never delete/log out shared Codex credentials or histories.
    public bool Remove(string id)
    {
        lock (_gate)
        {
            if (id == _loginProfile || !_services.TryGetValue(id, out var service) || service.IsRefreshing) return false;
            var profile = _configuration.Profiles.First(p => p.Id == id);
            var profiles = _configuration.Profiles.Where(p => p.Id != id).ToArray();
            Save(_configuration with { Profiles = profiles,
                SelectedId = _configuration.SelectedId == id ? profiles.FirstOrDefault()?.Id ?? "" : _configuration.SelectedId,
                IgnoredHomes = _configuration.IgnoredHomes.Append(profile.HomePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
            _services.Remove(id);
        }
        Changed?.Invoke();
        return true;
    }

    public bool ShouldRefresh(DateTimeOffset now, TimeSpan interval) => Accounts.Any(account =>
        !account.IsSigningIn && CodexQuotaService.ShouldRefreshOnFlyoutOpen(account.Snapshot, now, interval));

    public Task<CodexRefreshResult> RefreshAutomaticallyAsync(TimeSpan interval, CancellationToken token)
    {
        // All entry points join the same coordinator, including automatic checks.
        // Each service has its own failure cooldown; the batch itself never overlaps.
        lock (_gate)
        {
            if (!Refresh.IsRefreshing) _automaticInterval = interval;
            return Refresh.RefreshAsync(token);
        }
    }
    private TimeSpan? _automaticInterval;

    public Task<CodexRefreshResult> RefreshManuallyAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (!Refresh.IsRefreshing) _automaticInterval = null;
            return Refresh.RefreshAsync(token);
        }
    }

    private async Task<CodexRefreshResult> RefreshAllAsync(CancellationToken token)
    {
        CodexAccountProfile[] profiles;
        TimeSpan? interval;
        lock (_gate) { profiles = _configuration.Profiles.ToArray(); interval = _automaticInterval; }
        await Task.WhenAll(profiles.Select(async profile =>
        {
            await _processSlots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                CodexQuotaService? service;
                lock (_gate)
                {
                    if (profile.Id == _loginProfile || !_services.TryGetValue(profile.Id, out service)) return;
                }
                if (interval is { } age && !CodexQuotaService.ShouldRefreshOnFlyoutOpen(service.Snapshot, DateTimeOffset.Now, age)) return;
                await service.RefreshAsync(_configuredPath(), token).ConfigureAwait(false);
            }
            finally { _processSlots.Release(); }
        })).ConfigureAwait(false);
        return new(Snapshot, Snapshot.Status != CodexQuotaStatus.Available, null);
    }

    public async Task<CodexDiscoveryResult> DiscoverAsync(IEnumerable<string> homes, bool automatic, CancellationToken token)
    {
        await _discoveryGate.WaitAsync(token).ConfigureAwait(false);
        var added = 0;
        var failed = 0;
        var signedOut = 0;
        try
        {
            foreach (var home in CodexHomeDiscovery.Candidates(homes, Directory.Exists))
            {
                token.ThrowIfCancellationRequested();
                lock (_gate)
                    if (_configuration.Profiles.Any(p => string.Equals(p.HomePath, home, StringComparison.OrdinalIgnoreCase))
                        || (automatic && _configuration.IgnoredHomes.Contains(home, StringComparer.OrdinalIgnoreCase))) continue;
                var profile = new CodexAccountProfile(Guid.NewGuid().ToString("N"), home, "");
                var service = _createService(profile);
                await _processSlots.WaitAsync(token).ConfigureAwait(false);
                CodexAccountIdentity identity;
                try { identity = await service.ProbeAccountAsync(_configuredPath(), token).ConfigureAwait(false); }
                finally { _processSlots.Release(); }
                if (identity.Status != CodexQuotaStatus.Available)
                {
                    if (identity.Status == CodexQuotaStatus.SignedOut) signedOut++; else failed++;
                    continue;
                }
                lock (_gate)
                {
                    if (_configuration.Profiles.Any(p => string.Equals(p.HomePath, home, StringComparison.OrdinalIgnoreCase))) continue;
                    Save(_configuration with { Profiles = _configuration.Profiles.Append(profile).ToArray(),
                        SelectedId = _configuration.Profiles.Count == 0 ? profile.Id : _configuration.SelectedId,
                        IgnoredHomes = _configuration.IgnoredHomes.Where(p => !string.Equals(p, home, StringComparison.OrdinalIgnoreCase)).ToArray() });
                    AddService(profile, service);
                    added++;
                }
                Changed?.Invoke();
            }
            return new(added, failed, signedOut);
        }
        finally { _discoveryGate.Release(); }
    }

    public async Task<CodexLoginResult> LoginAsync(string? profileId, string label,
        Func<Uri, CancellationToken, Task> openBrowser, CancellationToken token)
    {
        if (!await _loginGate.WaitAsync(0, token).ConfigureAwait(false)) return new(CodexQuotaStatus.Unavailable);
        try
        {
            CodexQuotaService service;
            lock (_gate)
            {
                if (profileId is null)
                {
                    var profile = _store.NewManaged(label);
                    // Persist the reference before Codex logs in, so interrupted login is recoverable.
                    Save(_configuration with { Profiles = _configuration.Profiles.Append(profile).ToArray(),
                        SelectedId = _configuration.Profiles.Count == 0 ? profile.Id : _configuration.SelectedId });
                    service = AddService(profile);
                    profileId = profile.Id;
                }
                else if (!_services.TryGetValue(profileId, out service!)) return new(CodexQuotaStatus.Unavailable);
                _loginProfile = profileId;
            }
            Changed?.Invoke();
            var result = await service.LoginAsync(_configuredPath(), openBrowser, token).ConfigureAwait(false);
            if (result.Status == CodexQuotaStatus.Available)
            {
                await _processSlots.WaitAsync(token).ConfigureAwait(false);
                try { await service.RefreshAsync(_configuredPath(), token).ConfigureAwait(false); }
                finally { _processSlots.Release(); }
                lock (_gate)
                {
                    if (service.Snapshot.Status == CodexQuotaStatus.Available
                        && (!_services.TryGetValue(_configuration.SelectedId, out var selected)
                            || !CodexRingPresentation.From(selected.Snapshot).IsAvailable))
                        Save(_configuration with { SelectedId = profileId });
                }
            }
            return result;
        }
        finally
        {
            lock (_gate) _loginProfile = null;
            _loginGate.Release();
            Changed?.Invoke();
        }
    }

    public Task<CreditRedemptionOutcome> ConsumeCreditAsync(string profileId, string creditId, CancellationToken token)
    {
        lock (_gate) return profileId != _loginProfile && _services.TryGetValue(profileId, out var service)
            ? service.ConsumeCreditAsync(creditId, _configuredPath(), token)
            : Task.FromResult(CreditRedemptionOutcome.Unavailable);
    }

    private CodexQuotaService AddService(CodexAccountProfile profile, CodexQuotaService? service = null)
    {
        service ??= _createService(profile);
        _services.Add(profile.Id, service);
        service.Changed += _ => Changed?.Invoke();
        return service;
    }

    private void Save(CodexAccountConfiguration configuration)
    {
        _store.Save(configuration);
        _configuration = configuration;
    }
}
