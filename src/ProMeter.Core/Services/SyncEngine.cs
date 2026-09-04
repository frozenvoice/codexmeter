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
            var (periodStart, _) = QuotaPeriodCalculator.CurrentPeriod(settings, now, LastQuotaMetadata?.ResetAt);
            var minUpdate = periodStart.ToUnixTimeSeconds();

            LastStatus = AppSyncStatus.Syncing;
            Report("Scanning current quota period...");

            parsed += await SyncIndexAsync(provider, false, minUpdate, "chat", UsageSource.ConversationSync, coverage, cancellationToken);
            coverage.NormalChats = true;

            try
            {
                parsed += await SyncIndexAsync(provider, true, minUpdate, "archived", UsageSource.ArchivedSync, coverage, cancellationToken);
                coverage.ArchivedChats = true;
            }
            catch (Exception ex)
            {
                _log.Warn("archived index failed: " + AppLog.Sanitize(ex.Message));
            }

            try
            {
                var projects = await ExecuteWithRetry(() => provider.GetProjectsAsync(cancellationToken), "projects", cancellationToken);
                _log.Info($"projects={projects.Count}");
                foreach (var project in projects)
                {
                    parsed += await SyncProjectAsync(provider, project, minUpdate, coverage, cancellationToken);
                }

                coverage.Projects = true;
            }
            catch (Exception ex)
            {
                _log.Warn("projects failed: " + AppLog.Sanitize(ex.Message));
            }

            _store.SetState("last_index_sync", now.ToString("O"));
            LastSyncCompleted = now;
            LastCoverage = coverage;
            LastStatus = coverage.Confidence == CoverageConfidence.Incomplete
                ? AppSyncStatus.PartialData
                : AppSyncStatus.UpToDate;
            LastStatusDetail = $"parsed={parsed}";
            _consecutiveFailures = 0;
            IsPaused = false;
            _log.Info($"sync complete parsed={parsed} coverage={coverage.SummaryLabel}");
            Report("Up to date", parsedEvents: parsed);
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
        CancellationToken cancellationToken)
    {
        var items = archived
            ? await ExecuteWithRetry(() => provider.GetArchivedConversationIndexAsync(minUpdate, cancellationToken), "archived-index", cancellationToken)
            : await ExecuteWithRetry(() => provider.GetConversationIndexAsync(false, minUpdate, cancellationToken), "index", cancellationToken);

        _log.Info($"{source} index={items.Count}");
        return await ProcessItemsAsync(provider, items, source, usageSource, coverage, cancellationToken);
    }

    private async Task<int> SyncProjectAsync(
        IChatGptProvider provider,
        ProjectInfo project,
        double minUpdate,
        CoverageInfo coverage,
        CancellationToken cancellationToken)
    {
        var items = await ExecuteWithRetry(
            () => provider.GetProjectConversationsAsync(project.Id, minUpdate, cancellationToken),
            "project-index",
            cancellationToken);
        foreach (var item in items)
        {
            item.ProjectId = project.Id;
            item.Source = "project";
        }

        _log.Info($"project {project.Id} index={items.Count}");
        return await ProcessItemsAsync(provider, items, "project", UsageSource.ProjectSync, coverage, cancellationToken);
    }

    private async Task<int> ProcessItemsAsync(
        IChatGptProvider provider,
        IReadOnlyList<ConversationIndexItem> items,
        string source,
        UsageSource usageSource,
        CoverageInfo coverage,
        CancellationToken cancellationToken)
    {
        var changed = items.Count(NeedsBody);
        _log.Info($"{source} changed={changed}");
        var parsed = 0;
        var processed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!NeedsBody(item))
            {
                continue;
            }

            await _bodyLock.WaitAsync(cancellationToken);
            try
            {
                var body = await ExecuteWithRetry(
                    () => provider.GetConversationMessagesAsync(item.Id, cancellationToken),
                    "conversation",
                    cancellationToken);
                var result = _parser.Parse(body, new ConversationParseContext
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
                    coverage.Notes = "Provider schema mismatch on at least one conversation.";
                    LastStatus = AppSyncStatus.ProviderSchemaMismatch;
                    _log.Warn($"schema mismatch conversation={item.Id}");
                    foreach (var diagnostic in result.Diagnostics)
                    {
                        _log.Warn(diagnostic);
                    }
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
                _store.UpsertUsageEvents(result.Events);
                _store.UpsertConversation(new ConversationRecord
                {
                    ConversationId = item.Id,
                    UpdateTime = item.UpdateTime,
                    ProjectId = item.ProjectId,
                    Archived = item.Archived,
                    LastScanned = DateTimeOffset.UtcNow,
                    LastSeenUpdateTime = item.UpdateTime,
                    Source = source
                });
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
        if (existing is null)
        {
            return true;
        }

        return existing.LastSeenUpdateTime + 0.001 < item.UpdateTime || existing.LastScanned is null;
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
