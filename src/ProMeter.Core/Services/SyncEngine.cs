namespace ProMeter.Services;

public sealed class SyncEngine
{
    private readonly SqliteStore _store;
    private readonly ConversationParser _parser;
    private readonly ModelNormalizer _models;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _bodyLock = new(1, 1);
    private int _consecutiveFailures;

    public SyncEngine(SqliteStore store, ConversationParser parser, ModelNormalizer models, AppLog log)
    {
        _store = store;
        _parser = parser;
        _models = models;
        _log = log;
    }

    public bool IsPaused { get; private set; }
    public DateTimeOffset? PauseUntil { get; private set; }
    public CoverageInfo LastCoverage { get; private set; } = new();
    public AppSyncStatus LastStatus { get; private set; } = AppSyncStatus.Idle;
    public string? LastStatusDetail { get; private set; }
    public DateTimeOffset? LastSyncCompleted { get; private set; }
    public QuotaMetadata? LastQuotaMetadata { get; private set; }
    public AccountStatus? LastAccount { get; private set; }

    public event Action<SyncProgress>? ProgressChanged;

    public async Task<SyncOutcome> SyncAsync(
        IChatGptProvider provider,
        AppSettings settings,
        bool force,
        CancellationToken cancellationToken = default)
    {
        if (!force && IsPaused && PauseUntil is DateTimeOffset until && until > DateTimeOffset.UtcNow)
        {
            LastStatus = AppSyncStatus.RateLimited;
            LastStatusDetail = "Automatic sync paused after repeated failures.";
            return new SyncOutcome(LastStatus, LastStatusDetail, 0);
        }

        if (force)
        {
            IsPaused = false;
            PauseUntil = null;
            _consecutiveFailures = 0;
        }

        var coverage = new CoverageInfo();
        var parsed = 0;
        var worst = AppSyncStatus.UpToDate;
        try
        {
            Report("Detecting account...");
            LastStatus = AppSyncStatus.DetectingAccount;
            var account = await ExecuteWithRetry(() => provider.GetAccountStatusAsync(cancellationToken), "account", cancellationToken);
            LastAccount = account;
            if (!account.IsSignedIn)
            {
                LastStatus = AppSyncStatus.AuthenticationRequired;
                LastStatusDetail = "Sign in to ChatGPT to reconstruct account usage.";
                LastCoverage = coverage;
                return new SyncOutcome(LastStatus, LastStatusDetail, 0);
            }

            Report("Loading model catalog...");
            LastStatus = AppSyncStatus.LoadingCatalog;
            try
            {
                var catalog = await ExecuteWithRetry(() => provider.GetModelCatalogAsync(cancellationToken), "models", cancellationToken);
                _models.ObserveCatalog(catalog);
                foreach (var model in catalog)
                {
                    _store.ObserveModel(model.Slug, model.Title, "catalog");
                }

                _log.Info($"catalog models={catalog.Count}");
            }
            catch (ChatGptProviderException ex) when (!ex.IsUnauthorized)
            {
                _log.Warn($"catalog unavailable status={ex.Status}");
            }

            try
            {
                LastQuotaMetadata = await provider.TryGetQuotaMetadataAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _log.Warn("quota metadata unavailable: " + AppLog.Sanitize(ex.Message));
                LastQuotaMetadata = new QuotaMetadata();
            }

            var now = DateTimeOffset.UtcNow;
            var serverReset = LastQuotaMetadata is { MatchesGptProAllowance: true, ResetAt: not null }
                ? LastQuotaMetadata.ResetAt
                : null;
            var (periodStart, _) = QuotaPeriodCalculator.CurrentPeriod(settings, now, serverReset);
            var minUpdate = periodStart.ToUnixTimeSeconds();

            LastStatus = AppSyncStatus.Syncing;
            Report("Scanning current quota period...");

            parsed += await SyncIndexAsync(provider, false, minUpdate, "chat", UsageSource.ConversationSync, coverage, force, value => worst = Worse(worst, value), cancellationToken);
            coverage.NormalChats = !coverage.IndexIncomplete;

            try
            {
                parsed += await SyncIndexAsync(provider, true, minUpdate, "archived", UsageSource.ArchivedSync, coverage, force, value => worst = Worse(worst, value), cancellationToken);
                coverage.ArchivedChats = true;
            }
            catch (Exception ex)
            {
                coverage.IndexIncomplete = true;
                worst = Worse(worst, AppSyncStatus.PartialData);
                _log.Warn("archived index failed: " + AppLog.Sanitize(ex.Message));
            }

            try
            {
                var projects = await ExecuteWithRetry(() => provider.GetProjectsAsync(cancellationToken), "projects", cancellationToken);
                if (projects.Incomplete)
                {
                    coverage.IndexIncomplete = true;
                    worst = Worse(worst, AppSyncStatus.PartialData);
                }

                _log.Info($"projects={projects.Projects.Count}");
                foreach (var project in projects.Projects)
                {
                    parsed += await SyncProjectAsync(provider, project, minUpdate, coverage, force, value => worst = Worse(worst, value), cancellationToken);
                }

                coverage.Projects = true;
            }
            catch (Exception ex)
            {
                coverage.IndexIncomplete = true;
                worst = Worse(worst, AppSyncStatus.PartialData);
                _log.Warn("projects failed: " + AppLog.Sanitize(ex.Message));
            }

            _store.SetState("last_index_sync", now.ToString("O"));
            LastSyncCompleted = now;
            LastCoverage = coverage;
            LastStatus = coverage.Confidence == CoverageConfidence.Incomplete
                ? Worse(worst, AppSyncStatus.PartialData)
                : worst;
            if (LastStatus == AppSyncStatus.UpToDate && (coverage.ConversationIncomplete || coverage.IndexIncomplete || coverage.FailedConversations > 0))
            {
                LastStatus = AppSyncStatus.PartialData;
            }

            LastStatusDetail = $"parsed={parsed}";
            _consecutiveFailures = 0;
            IsPaused = false;
            _log.Info($"sync complete parsed={parsed} status={LastStatus} coverage={coverage.SummaryLabel}");
            Report(DisplayFormatting.StatusLabel(LastStatus), parsedEvents: parsed);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsUnauthorized)
        {
            LastStatus = AppSyncStatus.AuthenticationRequired;
            LastStatusDetail = "ChatGPT session expired.";
            _log.Http("sync", ex.Status, "auth required");
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.SchemaMismatch)
        {
            LastStatus = AppSyncStatus.ProviderSchemaMismatch;
            LastStatusDetail = "Provider schema mismatch";
            _log.Error("Provider schema mismatch");
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsRateLimited)
        {
            Pause(TimeSpan.FromMinutes(20));
            LastStatus = AppSyncStatus.RateLimited;
            LastStatusDetail = "Rate limited. Automatic sync paused.";
            _log.Http("sync", 429, "paused");
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsOffline)
        {
            LastStatus = AppSyncStatus.Offline;
            LastStatusDetail = "ChatGPT is unreachable.";
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            LastStatus = AppSyncStatus.Error;
            LastStatusDetail = AppLog.Sanitize(ex.Message);
            _log.Error("sync failed", ex);
            if (_consecutiveFailures >= 3)
            {
                Pause(TimeSpan.FromMinutes(30));
            }

            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
    }

    public void RecordImportedEvents(int count)
    {
        _log.Info($"import events applied={count}");
    }

    private async Task<int> SyncIndexAsync(
        IChatGptProvider provider,
        bool archived,
        double minUpdate,
        string source,
        UsageSource usageSource,
        CoverageInfo coverage,
        bool force,
        Action<AppSyncStatus> setWorst,
        CancellationToken cancellationToken)
    {
        var result = archived
            ? await ExecuteWithRetry(() => provider.GetArchivedConversationIndexAsync(minUpdate, cancellationToken), "archived-index", cancellationToken)
            : await ExecuteWithRetry(() => provider.GetConversationIndexAsync(false, minUpdate, cancellationToken), "index", cancellationToken);
        if (result.Incomplete)
        {
            coverage.IndexIncomplete = true;
            setWorst(AppSyncStatus.PartialData);
        }

        _log.Info($"{source} index={result.Items.Count} pages={result.Pages} incomplete={result.Incomplete}");
        return await ProcessItemsAsync(provider, result.Items, source, usageSource, coverage, force, setWorst, cancellationToken);
    }

    private async Task<int> SyncProjectAsync(
        IChatGptProvider provider,
        ProjectInfo project,
        double minUpdate,
        CoverageInfo coverage,
        bool force,
        Action<AppSyncStatus> setWorst,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteWithRetry(
            () => provider.GetProjectConversationsAsync(project.Id, minUpdate, cancellationToken),
            "project-index",
            cancellationToken);
        if (result.Incomplete)
        {
            coverage.IndexIncomplete = true;
            setWorst(AppSyncStatus.PartialData);
        }

        foreach (var item in result.Items)
        {
            item.ProjectId = project.Id;
            item.Source = "project";
        }

        _log.Info($"project {project.Id} index={result.Items.Count}");
        return await ProcessItemsAsync(provider, result.Items, "project", UsageSource.ProjectSync, coverage, force, setWorst, cancellationToken);
    }

    private async Task<int> ProcessItemsAsync(
        IChatGptProvider provider,
        IReadOnlyList<ConversationIndexItem> items,
        string source,
        UsageSource usageSource,
        CoverageInfo coverage,
        bool force,
        Action<AppSyncStatus> setWorst,
        CancellationToken cancellationToken)
    {
        var changed = items.Count(item => force || NeedsBody(item));
        _log.Info($"{source} changed={changed} force={force}");
        var parsed = 0;
        var processed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!force && !NeedsBody(item))
            {
                continue;
            }

            await _bodyLock.WaitAsync(cancellationToken);
            try
            {
                ConversationLoadResult load;
                try
                {
                    load = await ExecuteWithRetry(
                        () => provider.GetConversationMessagesAsync(item.Id, cancellationToken),
                        "conversation",
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    coverage.FailedConversations++;
                    coverage.ConversationIncomplete = true;
                    setWorst(AppSyncStatus.PartialData);
                    _store.RecordConversationFailure(_store.GetConversation(item.Id), item, ConversationScanStatus.FetchFailed, AppLog.Sanitize(ex.Message));
                    _log.Warn($"fetch failed conversation={item.Id}");
                    continue;
                }

                if (!load.Complete || load.SchemaMismatch || load.Conversation is null)
                {
                    var status = load.SchemaMismatch ? ConversationScanStatus.SchemaMismatch : ConversationScanStatus.Incomplete;
                    coverage.FailedConversations++;
                    coverage.ConversationIncomplete = true;
                    setWorst(load.SchemaMismatch ? AppSyncStatus.ProviderSchemaMismatch : AppSyncStatus.PartialData);
                    coverage.Notes = load.SchemaMismatch
                        ? "Provider schema mismatch on at least one conversation."
                        : "At least one conversation was incomplete.";
                    _store.RecordConversationFailure(_store.GetConversation(item.Id), item, status, string.Join("; ", load.Diagnostics));
                    foreach (var diagnostic in load.Diagnostics)
                    {
                        _log.Warn(diagnostic);
                    }

                    continue;
                }

                var result = _parser.Parse(load.Conversation, new ConversationParseContext
                {
                    ConversationId = item.Id,
                    ProjectId = item.ProjectId,
                    Archived = item.Archived,
                    Source = usageSource,
                    UpdateTime = item.UpdateTime,
                    FallbackCreatedAt = item.CreateTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds((long)item.CreateTime)
                        : null
                });

                if (result.SchemaMismatch)
                {
                    coverage.FailedConversations++;
                    coverage.ConversationIncomplete = true;
                    setWorst(AppSyncStatus.ProviderSchemaMismatch);
                    coverage.Notes = "Provider schema mismatch on at least one conversation.";
                    _store.RecordConversationFailure(_store.GetConversation(item.Id), item, ConversationScanStatus.SchemaMismatch, string.Join("; ", result.Diagnostics));
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        _log.Warn(diagnostic);
                    }

                    continue;
                }

                if (result.HasAlternateBranches)
                {
                    coverage.BranchesIncluded = true;
                }

                foreach (var usage in result.Events)
                {
                    if (!string.IsNullOrWhiteSpace(usage.RawModel))
                    {
                        _store.ObserveModel(usage.RawModel, usage.NormalizedModel, "conversation");
                    }
                }

                parsed += result.Events.Count;
                var now = DateTimeOffset.UtcNow;
                _store.ReconcileConversation(new ConversationRecord
                {
                    ConversationId = item.Id,
                    UpdateTime = item.UpdateTime,
                    ProjectId = item.ProjectId,
                    Archived = item.Archived,
                    LastScanned = now,
                    LastSeenUpdateTime = item.UpdateTime,
                    Source = source,
                    LastSuccessfulScan = now,
                    LastError = null,
                    LastErrorAt = null,
                    Status = ConversationScanStatus.Ok
                }, result.Events);
                processed++;
                Report("Scanning conversations...", items.Count, changed, processed, parsed);
            }
            finally
            {
                _bodyLock.Release();
            }
        }

        return parsed;
    }

    private bool NeedsBody(ConversationIndexItem item)
    {
        var existing = _store.GetConversation(item.Id);
        if (existing is null || existing.LastSuccessfulScan is null)
        {
            return true;
        }

        if (existing.Status is ConversationScanStatus.Incomplete or ConversationScanStatus.SchemaMismatch or ConversationScanStatus.FetchFailed or ConversationScanStatus.Unknown)
        {
            return true;
        }

        return existing.LastSeenUpdateTime + 0.001 < item.UpdateTime;
    }

    private static AppSyncStatus Worse(AppSyncStatus current, AppSyncStatus incoming)
    {
        static int Rank(AppSyncStatus status) => status switch
        {
            AppSyncStatus.ProviderSchemaMismatch => 5,
            AppSyncStatus.PartialData => 4,
            AppSyncStatus.Error => 3,
            AppSyncStatus.ApiChanged => 3,
            AppSyncStatus.UpToDate => 0,
            _ => 1
        };

        return Rank(incoming) > Rank(current) ? incoming : current;
    }

    private async Task<T> ExecuteWithRetry<T>(Func<Task<T>> action, string operation, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var result = await action();
                _log.Http(operation, 200);
                return result;
            }
            catch (ChatGptProviderException ex) when (ex.IsRateLimited || ex.IsServerError)
            {
                _log.Http(operation, ex.Status, $"attempt={attempt + 1}");
                var wait = ParseRetryAfter(ex.RetryAfter) ?? delay;
                await Task.Delay(wait, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 60_000));
                if (attempt == 5)
                {
                    throw;
                }
            }
        }

        throw new ChatGptProviderException("Retry exhausted.", 429);
    }

    private static TimeSpan? ParseRetryAfter(string? value)
    {
        if (int.TryParse(value, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 300));
        }

        return null;
    }

    private void Pause(TimeSpan duration)
    {
        IsPaused = true;
        PauseUntil = DateTimeOffset.UtcNow.Add(duration);
    }

    private void Report(string phase, int index = 0, int changed = 0, int processed = 0, int parsedEvents = 0)
    {
        ProgressChanged?.Invoke(new SyncProgress
        {
            Phase = phase,
            IndexCount = index,
            ChangedConversations = changed,
            ProcessedConversations = processed,
            ParsedEvents = parsedEvents
        });
    }
}

public sealed record SyncOutcome(AppSyncStatus Status, string? Detail, int ParsedEvents);
