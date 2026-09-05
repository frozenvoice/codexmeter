namespace ProMeter.Services;

public sealed class SyncEngine
{
    public const string MissingAssistantUsageDiagnostic =
        "Conversation history loaded but no assistant usage metadata was reconstructed.";
    public const string LastSyncCompletedStateKey = "last_sync_completed";

    private readonly SqliteStore _store;
    private readonly ConversationParser _parser;
    private readonly ModelNormalizer _models;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _bodyLock = new(1, 1);
    private int _consecutiveFailures;
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public int RetryAttempts { get; set; } = 6;
    public TimeSpan ConversationBodyTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int ConsecutiveBodyTimeoutLimit { get; set; } = 3;
    private int _bodyFetchDelayMs = 250;
    private bool _pacedBodyThisSync;
    private int _consecutiveBodyTimeouts;
    private ReconstructionTotals _reconstruction = new();
    private readonly List<DeferredZeroEventScan> _deferredZeroEvents = [];
    private readonly Dictionary<string, ConversationWorkEntry> _work = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredIds = new(StringComparer.Ordinal);
    private readonly IClock _clock;

    public SyncEngine(SqliteStore store, ConversationParser parser, ModelNormalizer models, AppLog log, IClock? clock = null)
    {
        _store = store;
        _parser = parser;
        _models = models;
        _log = log;
        _clock = clock ?? SystemClock.Instance;
        LastSyncCompleted = RestoreLastSyncCompleted();
    }

    public bool IsPaused { get; private set; }
    public DateTimeOffset? PauseUntil { get; private set; }
    public CoverageInfo LastCoverage { get; private set; } = new();
    public AppSyncStatus LastStatus { get; private set; } = AppSyncStatus.Idle;
    public string? LastStatusDetail { get; private set; }
    public DateTimeOffset? LastSyncCompleted { get; private set; }
    public QuotaMetadataSet? LastQuotaMetadata { get; private set; }
    public AccountStatus? LastAccount { get; private set; }

    public event Action<SyncProgress>? ProgressChanged;

    public async Task<SyncOutcome> SyncAsync(
        IChatGptProvider provider,
        AppSettings settings,
        bool force,
        CancellationToken cancellationToken = default)
    {
        return await SyncAsync(
            provider,
            settings,
            new SyncRunOptions { BypassPause = force, ForceBodyRescan = force },
            cancellationToken);
    }

    public async Task<SyncOutcome> SyncAsync(
        IChatGptProvider provider,
        AppSettings settings,
        bool force,
        SyncRunOptions options,
        CancellationToken cancellationToken = default)
    {
        return await SyncAsync(
            provider,
            settings,
            options with
            {
                BypassPause = options.BypassPause || force,
                ForceBodyRescan = options.ForceBodyRescan || force
            },
            cancellationToken);
    }

    public async Task<SyncOutcome> SyncAsync(
        IChatGptProvider provider,
        AppSettings settings,
        SyncRunOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!options.BypassPause && IsPaused && PauseUntil is DateTimeOffset until && until > _clock.UtcNow)
        {
            LastStatus = AppSyncStatus.RateLimited;
            LastStatusDetail = UiText.AutoSyncPaused;
            return new SyncOutcome(LastStatus, LastStatusDetail, 0);
        }

        if (options.BypassPause)
        {
            IsPaused = false;
            PauseUntil = null;
            _consecutiveFailures = 0;
        }

        _log.Info(
            $"sync plan origin={options.Origin.ToString().ToLowerInvariant()} bypassPause={options.BypassPause} forceBodyRescan={options.ForceBodyRescan}");

        var coverage = new CoverageInfo();
        var parsed = 0;
        var worst = AppSyncStatus.UpToDate;
        _bodyFetchDelayMs = Math.Clamp(settings.BodyFetchDelayMilliseconds, 0, 5000);
        _pacedBodyThisSync = false;
        _consecutiveBodyTimeouts = 0;
        _reconstruction = new ReconstructionTotals();
        _deferredZeroEvents.Clear();
        _work.Clear();
        _deferredIds.Clear();
        try
        {
            Report(UiText.DetectingAccount);
            LastStatus = AppSyncStatus.DetectingAccount;
            var account = await ExecuteWithRetry(() => provider.GetAccountStatusAsync(cancellationToken), "account", cancellationToken);
            LastAccount = account;
            if (!account.IsSignedIn)
            {
                LastStatus = AppSyncStatus.SignedOut;
                LastStatusDetail = UiText.ChatGptSignedOut;
                LastCoverage = coverage;
                LogSyncFailure(options.Origin, LastStatus);
                return new SyncOutcome(LastStatus, LastStatusDetail, 0);
            }

            Report(UiText.LoadingCatalog);
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
            catch (ChatGptProviderException ex) when (IsFatalProviderError(ex))
            {
                throw;
            }
            catch (ChatGptProviderException ex)
            {
                _log.Warn($"catalog unavailable status={ex.Status}");
            }

            try
            {
                LastQuotaMetadata = await provider.TryGetQuotaMetadataAsync(cancellationToken);
            }
            catch (ChatGptProviderException ex) when (IsFatalProviderError(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.Warn("quota metadata unavailable: " + AppLog.Sanitize(ex.Message));
                LastQuotaMetadata = new QuotaMetadataSet();
            }

            var periodReference = _clock.UtcNow;
            var proStatus = LastQuotaMetadata?.ProServerStatus;
            var proReset = proStatus?.ResetConfidence == ServerResetConfidence.Server ? proStatus.ResetAt : null;
            var serverReset = proReset ?? LastQuotaMetadata?.WeeklyWindow(settings.PlanPreset)?.ResetAt;
            var (calculatedPeriodStart, _) = QuotaPeriodCalculator.CurrentPeriod(settings, periodReference, serverReset);
            var periodStart = options.PeriodStartOverride ?? calculatedPeriodStart;
            var minUpdate = periodStart.ToUnixTimeSeconds();

            LastStatus = AppSyncStatus.Syncing;
            Report(UiText.ScanningPeriod);

            parsed += await SyncIndexAsync(provider, false, minUpdate, "chat", UsageSource.ConversationSync, coverage, options, value => worst = Worse(worst, value), cancellationToken);

            try
            {
                parsed += await SyncIndexAsync(provider, true, minUpdate, "archived", UsageSource.ArchivedSync, coverage, options, value => worst = Worse(worst, value), cancellationToken);
            }
            catch (ChatGptProviderException ex) when (IsFatalProviderError(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                coverage.IndexIncomplete = true;
                coverage.ArchivedIndexState = CollectionState.Failed;
                coverage.ArchivedChats = false;
                worst = Worse(worst, AppSyncStatus.PartialData);
                _log.Warn("archived index failed: " + AppLog.Sanitize(ex.Message));
            }

            try
            {
                var projects = await ExecuteWithRetry(() => provider.GetProjectsAsync(cancellationToken), "projects", cancellationToken);
                if (projects.SchemaMismatch)
                {
                    coverage.IndexIncomplete = true;
                    coverage.ProjectsIndexState = CollectionState.Failed;
                    coverage.Projects = false;
                    worst = Worse(worst, AppSyncStatus.ProviderSchemaMismatch);
                }
                else if (projects.Incomplete)
                {
                    coverage.IndexIncomplete = true;
                    coverage.ProjectsIndexState = CollectionState.Partial;
                    coverage.Projects = false;
                    worst = Worse(worst, AppSyncStatus.PartialData);
                }
                else
                {
                    coverage.ProjectsIndexState = CollectionState.Complete;
                }

                _log.Info($"projects={projects.Projects.Count}");
                if (!projects.SchemaMismatch)
                {
                    foreach (var project in projects.Projects)
                    {
                        parsed += await SyncProjectAsync(provider, project, minUpdate, coverage, options, value => worst = Worse(worst, value), cancellationToken);
                    }

                    coverage.Projects = !projects.Incomplete;
                }
            }
            catch (ChatGptProviderException ex) when (IsFatalProviderError(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                coverage.IndexIncomplete = true;
                coverage.ProjectsIndexState = CollectionState.Failed;
                coverage.Projects = false;
                worst = Worse(worst, AppSyncStatus.PartialData);
                _log.Warn("projects failed: " + AppLog.Sanitize(ex.Message));
            }

            cancellationToken.ThrowIfCancellationRequested();
            _store.SetState("last_index_sync", periodReference.ToString("O"));
            ApplyZeroEventDiagnostics(coverage, value => worst = Worse(worst, value));
            LastCoverage = coverage;
            LastStatus = coverage.Confidence == CoverageConfidence.Incomplete
                ? Worse(worst, AppSyncStatus.PartialData)
                : worst;
            if (LastStatus == AppSyncStatus.UpToDate && (coverage.ConversationIncomplete || coverage.IndexIncomplete || coverage.FailedConversations > 0))
            {
                LastStatus = AppSyncStatus.PartialData;
            }

            LastStatusDetail = coverage.HistoryLoadedWithoutUsage
                ? MissingAssistantUsageDiagnostic
                : $"parsed={parsed}";
            _consecutiveFailures = 0;
            IsPaused = false;
            if (SyncFailurePresentation.IsLoggedFailure(LastStatus))
            {
                LogSyncFailure(options.Origin, LastStatus);
            }

            _log.Info($"sync complete parsed={parsed} status={LastStatus} coverage={coverage.SummaryLabel}");
            _log.Info(
                "sync diagnostics" +
                $" loaded={_reconstruction.Loaded}" +
                $" failed={_reconstruction.Failed}" +
                $" scanAttempts={_reconstruction.ScanAttempts}" +
                $" uniqueConversations={_reconstruction.UniqueConversations}" +
                $" uniqueFailed={_reconstruction.Failed}" +
                $" bodyFetches={_reconstruction.BodyFetches}" +
                $" withEvents={_reconstruction.WithEvents}" +
                $" zeroEvents={_reconstruction.ZeroEvents}" +
                $" assistantNodes={_reconstruction.AssistantLikeNodes}" +
                $" modelMetadataNodes={_reconstruction.NodesWithModelMetadata}" +
                $" families GptPro={FamilyCount(QuotaFamily.GptPro)}" +
                $" SolReasoning={FamilyCount(QuotaFamily.SolReasoning)}" +
                $" Instant={FamilyCount(QuotaFamily.Instant)}" +
                $" Unknown={FamilyCount(QuotaFamily.Unknown)}");
            Report(DisplayFormatting.StatusLabel(LastStatus), parsedEvents: parsed);
            cancellationToken.ThrowIfCancellationRequested();
            var completed = _clock.UtcNow.ToUniversalTime();
            _store.SetState(LastSyncCompletedStateKey, completed.ToString("O", CultureInfo.InvariantCulture));
            LastSyncCompleted = completed;
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsUnauthorized)
        {
            LastStatus = AppSyncStatus.AuthenticationRequired;
            LastStatusDetail = UiText.ChatGptSessionExpired;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsForbidden)
        {
            LastStatus = AppSyncStatus.Forbidden;
            LastStatusDetail = CompanionDiagnostics.Forbidden403;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsChatGptTabRequired)
        {
            LastStatus = AppSyncStatus.ChatGptTabRequired;
            LastStatusDetail = CompanionDiagnostics.NoChatGptTab;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsPageBridgeUnavailable)
        {
            LastStatus = AppSyncStatus.PageBridgeUnavailable;
            LastStatusDetail = CompanionDiagnostics.PageBridgeUnavailable;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.SchemaMismatch)
        {
            LastStatus = AppSyncStatus.ProviderSchemaMismatch;
            LastStatusDetail = UiText.SchemaMismatchStatus;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsRateLimited)
        {
            Pause(TimeSpan.FromMinutes(20));
            LastStatus = AppSyncStatus.RateLimited;
            LastStatusDetail = UiText.RateLimitedPaused;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsCompanionDisconnected)
        {
            LastStatus = AppSyncStatus.CompanionDisconnected;
            LastStatusDetail = UiText.CompanionDisconnectedStatus;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsBridgeTimeout)
        {
            LastStatus = AppSyncStatus.BridgeTimeout;
            LastStatusDetail = UiText.BridgeTimeoutStatus;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsBridgeWriteFailed)
        {
            LastStatus = AppSyncStatus.BridgeWriteFailed;
            LastStatusDetail = UiText.BridgeWriteFailedStatus;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsOffline)
        {
            LastStatus = AppSyncStatus.Offline;
            LastStatusDetail = UiText.ChatGptUnreachable;
            LogSyncFailure(options.Origin, LastStatus);
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (OperationCanceledException)
        {
            LastStatus = AppSyncStatus.Error;
            LastStatusDetail = "cancelled";
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            LastStatus = AppSyncStatus.Error;
            LastStatusDetail = AppLog.Sanitize(ex.Message);
            LogSyncFailure(options.Origin, LastStatus);
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

    private void LogSyncFailure(SyncOrigin origin, AppSyncStatus category) =>
        _log.Warn(SyncFailurePresentation.LogLine(origin, category));

    private DateTimeOffset? RestoreLastSyncCompleted()
    {
        var stored = _store.GetState(LastSyncCompletedStateKey);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (DateTimeOffset.TryParseExact(
                stored,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var restored))
        {
            return restored.ToUniversalTime();
        }

        _log.Warn("Ignoring malformed persisted last-sync completion timestamp.");
        return null;
    }

    private async Task<int> SyncIndexAsync(
        IChatGptProvider provider,
        bool archived,
        double minUpdate,
        string source,
        UsageSource usageSource,
        CoverageInfo coverage,
        SyncRunOptions options,
        Action<AppSyncStatus> setWorst,
        CancellationToken cancellationToken)
    {
        var result = archived
            ? await ExecuteWithRetry(() => provider.GetArchivedConversationIndexAsync(minUpdate, cancellationToken), "archived-index", cancellationToken)
            : await ExecuteWithRetry(() => provider.GetConversationIndexAsync(false, minUpdate, cancellationToken), "index", cancellationToken);
        var state = result.SchemaMismatch
            ? CollectionState.Failed
            : result.Incomplete || result.TimestampIncomplete
                ? CollectionState.Partial
                : CollectionState.Complete;
        if (result.SchemaMismatch)
        {
            coverage.IndexIncomplete = true;
            setWorst(AppSyncStatus.ProviderSchemaMismatch);
        }
        else if (result.Incomplete || result.TimestampIncomplete)
        {
            coverage.IndexIncomplete = true;
            if (result.TimestampIncomplete)
            {
                coverage.ConversationIncomplete = true;
            }

            setWorst(AppSyncStatus.PartialData);
        }

        if (archived)
        {
            coverage.ArchivedChats = state == CollectionState.Complete;
            coverage.ArchivedIndexState = state;
        }
        else
        {
            coverage.NormalChats = state == CollectionState.Complete;
            coverage.NormalIndexState = state;
        }

        _log.Info($"{source} index={result.Items.Count} pages={result.Pages} incomplete={result.Incomplete} mismatch={result.SchemaMismatch}");
        if (result.SchemaMismatch)
        {
            return 0;
        }

        return await ProcessItemsAsync(provider, result.Items, source, usageSource, coverage, options, setWorst, cancellationToken);
    }

    private async Task<int> SyncProjectAsync(
        IChatGptProvider provider,
        ProjectInfo project,
        double minUpdate,
        CoverageInfo coverage,
        SyncRunOptions options,
        Action<AppSyncStatus> setWorst,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteWithRetry(
            () => provider.GetProjectConversationsAsync(project.Id, minUpdate, cancellationToken),
            "project-index",
            cancellationToken);
        if (result.SchemaMismatch)
        {
            coverage.IndexIncomplete = true;
            coverage.ProjectsIndexState = CollectionState.Failed;
            setWorst(AppSyncStatus.ProviderSchemaMismatch);
            return 0;
        }

        if (result.Incomplete || result.TimestampIncomplete)
        {
            coverage.IndexIncomplete = true;
            if (coverage.ProjectsIndexState != CollectionState.Failed)
            {
                coverage.ProjectsIndexState = CollectionState.Partial;
            }

            if (result.TimestampIncomplete)
            {
                coverage.ConversationIncomplete = true;
            }

            setWorst(AppSyncStatus.PartialData);
        }

        foreach (var item in result.Items)
        {
            item.ProjectId = project.Id;
            item.Source = "project";
        }

        _log.Info($"project {project.Id} index={result.Items.Count}");
        return await ProcessItemsAsync(provider, result.Items, "project", UsageSource.ProjectSync, coverage, options, setWorst, cancellationToken);
    }

    private async Task<int> ProcessItemsAsync(
        IChatGptProvider provider,
        IReadOnlyList<ConversationIndexItem> items,
        string source,
        UsageSource usageSource,
        CoverageInfo coverage,
        SyncRunOptions options,
        Action<AppSyncStatus> setWorst,
        CancellationToken cancellationToken)
    {
        var changed = items.Count(item => options.ForceBodyRescan || NeedsBody(item));
        _log.Info($"{source} changed={changed} forceBodyRescan={options.ForceBodyRescan} bypassPause={options.BypassPause}");
        _reconstruction.IndexedConversations += items.Count;
        var parsed = 0;
        var processed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_work.TryGetValue(item.Id, out var existing))
            {
                if (options.ForceBodyRescan || NeedsBody(item))
                {
                    _reconstruction.ScanAttempts++;
                }

                MergeAppearance(existing, item);
                continue;
            }

            if (!options.ForceBodyRescan && !NeedsBody(item))
            {
                var record = _store.GetConversation(item.Id);
                if (ConversationFetchBackoff.IsDeferredFailure(item, record, _clock.UtcNow, false))
                {
                    MarkDeferred(coverage, item.Id, record, setWorst);
                }

                continue;
            }

            _reconstruction.ScanAttempts++;
            _reconstruction.UniqueConversations++;
            var entry = new ConversationWorkEntry { First = item, UsageSource = usageSource };
            _work[item.Id] = entry;

            await PaceBodyFetchAsync(cancellationToken);
            await _bodyLock.WaitAsync(cancellationToken);
            try
            {
                ConversationLoadResult load;
                try
                {
                    _reconstruction.BodyFetches++;
                    using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    if (ConversationBodyTimeout > TimeSpan.Zero)
                    {
                        bodyCts.CancelAfter(ConversationBodyTimeout);
                    }

                    load = await ExecuteWithRetry(
                        () => provider.GetConversationMessagesAsync(item.Id, bodyCts.Token),
                        "conversation",
                        bodyCts.Token);
                    entry.BodyFetched = true;
                    _consecutiveBodyTimeouts = 0;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    if (RecordBodyTimeoutAndMaybeAbort(item, entry, coverage, setWorst))
                    {
                        throw new ChatGptProviderException(CompanionBridgeProtocol.TimeoutError);
                    }

                    continue;
                }
                catch (ChatGptProviderException ex) when (ex.IsBridgeTimeout)
                {
                    if (RecordBodyTimeoutAndMaybeAbort(item, entry, coverage, setWorst))
                    {
                        throw;
                    }

                    continue;
                }
                catch (ChatGptProviderException ex) when (IsSyncAbortingBodyFailure(ex))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var (category, status) = SyncFailureClassifier.Classify(ex);
                    MarkUniqueFailure(coverage, entry, setWorst, AppSyncStatus.PartialData, category, status);
                    RecordBodyFailure(item, ConversationScanStatus.FetchFailed, AppLog.Sanitize(ex.Message), category, status);
                    LogConversationFailure(item.Id, category, status);
                    continue;
                }

                if (!load.Complete || load.SchemaMismatch || load.Conversation is null)
                {
                    var scanStatus = load.SchemaMismatch ? ConversationScanStatus.SchemaMismatch : ConversationScanStatus.Incomplete;
                    var worst = load.SchemaMismatch ? AppSyncStatus.ProviderSchemaMismatch : AppSyncStatus.PartialData;
                    var category = SyncFailureClassifier.ClassifyLoad(load);
                    MarkUniqueFailure(coverage, entry, setWorst, worst, category);
                    coverage.Notes = load.SchemaMismatch
                        ? "Provider schema mismatch on at least one conversation."
                        : "At least one conversation was incomplete.";
                    RecordBodyFailure(item, scanStatus, string.Join("; ", load.Diagnostics), category);
                    LogConversationFailure(item.Id, category, 0);
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
                    MarkUniqueFailure(coverage, entry, setWorst, AppSyncStatus.ProviderSchemaMismatch, ConversationFetchBackoff.SchemaMismatch);
                    coverage.Notes = "Provider schema mismatch on at least one conversation.";
                    RecordBodyFailure(item, ConversationScanStatus.SchemaMismatch, string.Join("; ", result.Diagnostics), ConversationFetchBackoff.SchemaMismatch);
                    LogConversationFailure(item.Id, "SchemaMismatch", 0);
                    continue;
                }

                ObserveParse(result);
                if (result.Events.Count == 0)
                {
                    _reconstruction.ZeroEvents++;
                    entry.DeferredZero = true;
                    _deferredZeroEvents.Add(new DeferredZeroEventScan
                    {
                        Item = item,
                        Result = result,
                        Source = source
                    });
                    processed++;
                    Report(UiText.ScanningConversations, items.Count, changed, processed, parsed);
                    continue;
                }

                parsed += CommitSuccessfulParse(item, result, source, coverage, setWorst);
                entry.Succeeded = true;
                processed++;
                Report(UiText.ScanningConversations, items.Count, changed, processed, parsed);
            }
            finally
            {
                _bodyLock.Release();
            }
        }

        return parsed;
    }

    private void MergeAppearance(ConversationWorkEntry existing, ConversationIndexItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ProjectId))
        {
            existing.First.ProjectId ??= item.ProjectId;
        }

        if (item.Archived)
        {
            existing.First.Archived = true;
        }

        if (string.Equals(item.Source, "project", StringComparison.Ordinal))
        {
            existing.First.Source = item.Source;
            existing.UsageSource = UsageSource.ProjectSync;
        }

        _store.MergeConversationMetadata(item.Id, item.ProjectId, item.Archived, item.Source);
    }

    private void MarkUniqueFailure(
        CoverageInfo coverage,
        ConversationWorkEntry entry,
        Action<AppSyncStatus> setWorst,
        AppSyncStatus status,
        string? category = null,
        int httpStatus = 0)
    {
        if (!entry.Failed)
        {
            entry.Failed = true;
            _reconstruction.Failed++;
            coverage.FailedConversations++;
            coverage.FailureSummary.AddThisSync(category, httpStatus);
        }

        coverage.ConversationIncomplete = true;
        setWorst(status);
    }

    private void MarkDeferred(
        CoverageInfo coverage,
        string conversationId,
        ConversationRecord? existing,
        Action<AppSyncStatus> setWorst)
    {
        if (!_deferredIds.Add(conversationId))
        {
            return;
        }

        coverage.FailedConversations++;
        coverage.ConversationIncomplete = true;
        coverage.FailureSummary.AddDeferred(existing?.LastFetchFailureCategory);
        setWorst(AppSyncStatus.PartialData);
    }

    private void RecordBodyFailure(
        ConversationIndexItem item,
        ConversationScanStatus status,
        string error,
        string? category,
        int httpStatus = 0)
    {
        _store.RecordConversationFailure(
            _store.GetConversation(item.Id),
            item,
            status,
            error,
            category,
            _clock.UtcNow,
            httpStatus);
    }

    private void LogConversationFailure(string conversationId, string category, int status)
    {
        _log.Warn($"conversation fetch failed id={conversationId} category={category} status={status}");
    }

    private void ObserveParse(ParseResult result)
    {
        _reconstruction.Loaded++;
        _reconstruction.AssistantLikeNodes += result.AssistantLikeNodeCount;
        _reconstruction.NodesWithModelMetadata += result.NodesWithModelMetadata;
        if (result.Events.Count > 0)
        {
            _reconstruction.WithEvents++;
        }

        foreach (var usage in result.Events)
        {
            _reconstruction.AddFamily(usage.QuotaFamily);
        }
    }

    private int CommitSuccessfulParse(
        ConversationIndexItem item,
        ParseResult result,
        string source,
        CoverageInfo coverage,
        Action<AppSyncStatus> setWorst)
    {
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

        var now = _clock.UtcNow;
        var trusted = item.UpdateTime > 0;
        _store.ReconcileConversation(new ConversationRecord
        {
            ConversationId = item.Id,
            UpdateTime = trusted ? item.UpdateTime : 0,
            ProjectId = item.ProjectId,
            Archived = item.Archived,
            LastScanned = now,
            LastSeenUpdateTime = trusted ? item.UpdateTime : 0,
            Source = source,
            LastSuccessfulScan = trusted ? now : null,
            LastError = trusted ? null : "missing update_time",
            LastErrorAt = trusted ? null : now,
            Status = trusted ? ConversationScanStatus.Ok : ConversationScanStatus.Incomplete,
            ConsecutiveFetchFailures = 0,
            NextEligibleFetchAt = null,
            LastFetchFailureCategory = null,
            LastAttemptedUpdateTime = trusted ? item.UpdateTime : 0,
            FetchFailureParserVersion = 0
        }, result.Events);
        if (!trusted)
        {
            coverage.ConversationIncomplete = true;
            setWorst(AppSyncStatus.PartialData);
        }

        return result.Events.Count;
    }

    private void ApplyZeroEventDiagnostics(CoverageInfo coverage, Action<AppSyncStatus> setWorst)
    {
        coverage.LoadedConversations = _reconstruction.Loaded;
        coverage.ConversationsWithEvents = _reconstruction.WithEvents;
        coverage.ZeroEventConversations = _reconstruction.ZeroEvents;
        coverage.AssistantLikeNodes = _reconstruction.AssistantLikeNodes;
        coverage.NodesWithModelMetadata = _reconstruction.NodesWithModelMetadata;
        coverage.ScanAttempts = _reconstruction.ScanAttempts;
        coverage.UniqueConversations = _reconstruction.UniqueConversations;
        coverage.BodyFetches = _reconstruction.BodyFetches;

        var suspect = _reconstruction.IndexedConversations > 0
                      && _reconstruction.Loaded >= 2
                      && _reconstruction.WithEvents == 0;
        if (suspect)
        {
            coverage.HistoryLoadedWithoutUsage = true;
            coverage.ConversationIncomplete = true;
            coverage.Notes = MissingAssistantUsageDiagnostic;
            setWorst(AppSyncStatus.ProviderSchemaMismatch);
            foreach (var deferred in _deferredZeroEvents)
            {
                if (_work.TryGetValue(deferred.Item.Id, out var entry) && entry.Failed)
                {
                    continue;
                }

                if (_work.TryGetValue(deferred.Item.Id, out var work))
                {
                    MarkUniqueFailure(coverage, work, setWorst, AppSyncStatus.ProviderSchemaMismatch, ConversationFetchBackoff.SchemaMismatch);
                }
                else
                {
                    coverage.FailedConversations++;
                    coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
                    _reconstruction.Failed++;
                }

                RecordBodyFailure(
                    deferred.Item,
                    ConversationScanStatus.SchemaMismatch,
                    MissingAssistantUsageDiagnostic,
                    ConversationFetchBackoff.SchemaMismatch);
            }

            return;
        }

        foreach (var deferred in _deferredZeroEvents)
        {
            CommitSuccessfulParse(deferred.Item, deferred.Result, deferred.Source, coverage, setWorst);
        }
    }

    private int FamilyCount(QuotaFamily family) =>
        _reconstruction.Families.TryGetValue(family, out var count) ? count : 0;

    private bool NeedsBody(ConversationIndexItem item) =>
        ConversationFetchBackoff.ShouldFetch(item, _store.GetConversation(item.Id), _clock.UtcNow, forceBodyRescan: false);

    private async Task PaceBodyFetchAsync(CancellationToken cancellationToken)
    {
        if (!_pacedBodyThisSync)
        {
            _pacedBodyThisSync = true;
            return;
        }

        if (_bodyFetchDelayMs <= 0)
        {
            return;
        }

        var jitter = Random.Shared.Next(0, (_bodyFetchDelayMs / 2) + 1);
        await Task.Delay(_bodyFetchDelayMs + jitter, cancellationToken);
    }

    private static bool IsFatalProviderError(ChatGptProviderException ex) =>
        ex.IsFatalTransportFailure;

    private static bool IsSyncAbortingBodyFailure(ChatGptProviderException ex) =>
        ex.IsUnauthorized
        || ex.IsForbidden
        || ex.IsRateLimited
        || ex.IsOffline
        || ex.IsChatGptTabRequired
        || ex.IsPageBridgeUnavailable
        || ex.IsCompanionDisconnected
        || ex.IsBridgeWriteFailed;

    private bool RecordBodyTimeoutAndMaybeAbort(
        ConversationIndexItem item,
        ConversationWorkEntry entry,
        CoverageInfo coverage,
        Action<AppSyncStatus> setWorst)
    {
        _consecutiveBodyTimeouts++;
        MarkUniqueFailure(coverage, entry, setWorst, AppSyncStatus.PartialData, ConversationFetchBackoff.BodyTimeout);
        RecordBodyFailure(
            item,
            ConversationScanStatus.FetchFailed,
            "conversation body timed out",
            ConversationFetchBackoff.BodyTimeout);
        LogConversationFailure(item.Id, "BodyTimeout", 0);
        return _consecutiveBodyTimeouts >= Math.Max(1, ConsecutiveBodyTimeoutLimit);
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
        var delay = RetryBaseDelay;
        var attempts = Math.Max(1, RetryAttempts);
        for (var attempt = 0; attempt < attempts; attempt++)
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
                if (attempt == attempts - 1)
                {
                    throw;
                }

                var wait = ParseRetryAfter(ex.RetryAfter) ?? delay;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, cancellationToken);
                }

                delay = TimeSpan.FromMilliseconds(Math.Min(Math.Max(delay.TotalMilliseconds, 1) * 2, 60_000));
            }
        }

        throw new ChatGptProviderException("Retry exhausted.", 429);
    }

    private static TimeSpan? ParseRetryAfter(string? value)
    {
        if (int.TryParse(value, out var seconds))
        {
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 300));
        }

        return null;
    }

    private void Pause(TimeSpan duration)
    {
        IsPaused = true;
        PauseUntil = _clock.UtcNow.Add(duration);
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

    private sealed class ReconstructionTotals
    {
        public int IndexedConversations { get; set; }
        public int Loaded { get; set; }
        public int Failed { get; set; }
        public int ScanAttempts { get; set; }
        public int UniqueConversations { get; set; }
        public int BodyFetches { get; set; }
        public int WithEvents { get; set; }
        public int ZeroEvents { get; set; }
        public int AssistantLikeNodes { get; set; }
        public int NodesWithModelMetadata { get; set; }
        public Dictionary<QuotaFamily, int> Families { get; } = [];

        public void AddFamily(QuotaFamily family)
        {
            Families.TryGetValue(family, out var count);
            Families[family] = count + 1;
        }
    }

    private sealed class DeferredZeroEventScan
    {
        public required ConversationIndexItem Item { get; init; }
        public required ParseResult Result { get; init; }
        public required string Source { get; init; }
    }

    private sealed class ConversationWorkEntry
    {
        public required ConversationIndexItem First { get; init; }
        public UsageSource UsageSource { get; set; }
        public bool BodyFetched { get; set; }
        public bool Failed { get; set; }
        public bool Succeeded { get; set; }
        public bool DeferredZero { get; set; }
    }
}

public sealed record SyncOutcome(AppSyncStatus Status, string? Detail, int ParsedEvents);

public sealed record SyncRunOptions
{
    public DateTimeOffset? PeriodStartOverride { get; init; }
    public SyncOrigin Origin { get; init; } = SyncOrigin.Auto;
    public bool BypassPause { get; init; }
    public bool ForceBodyRescan { get; init; }

    public static SyncRunOptions Auto { get; } = new() { Origin = SyncOrigin.Auto };

    public static SyncRunOptions ManualIncremental { get; } = Manual(bypassPause: true);

    public static SyncRunOptions StartupIncremental { get; } = new()
    {
        Origin = SyncOrigin.Startup,
        BypassPause = true
    };

    public static SyncRunOptions FlyoutStale { get; } = new() { Origin = SyncOrigin.FlyoutStaleRefresh };

    public static SyncRunOptions Manual(bool bypassPause) => new()
    {
        Origin = SyncOrigin.Manual,
        BypassPause = bypassPause
    };
}
