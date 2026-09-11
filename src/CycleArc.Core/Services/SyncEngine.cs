namespace CycleArc.Services;

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
    public TimeSpan ConversationBodyTimeout { get; set; } = TimeSpan.FromSeconds(45);
    public int ConsecutiveBodyTimeoutLimit { get; set; } = 3;
    private int _bodyFetchDelayMs = 250;
    private bool _pacedBodyThisSync;
    private int _consecutiveBodyTimeouts;
    private ReconstructionTotals _reconstruction = new();
    private int _reconstructionRevalidations;
    private readonly List<DeferredZeroEventScan> _deferredZeroEvents = [];
    private readonly Dictionary<string, ConversationWorkEntry> _work = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferredIds = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly ReconstructionLedger _ledger;

    public SyncEngine(SqliteStore store, ConversationParser parser, ModelNormalizer models, AppLog log, IClock? clock = null)
    {
        _store = store;
        _parser = parser;
        _models = models;
        _log = log;
        _clock = clock ?? SystemClock.Instance;
        _ledger = new ReconstructionLedger(models, _clock);
        LastSyncCompleted = RestoreLastSyncCompleted();
        ConversationSchemaSystemicFailureLatched = RestoreConversationSchemaLatch();
    }

    public bool ConversationSchemaSystemicFailureLatched { get; private set; }

    /// <summary>Reconstruction revalidation slots actually claimed by the last sync.</summary>
    public int LastReconstructionRevalidations => _reconstructionRevalidations;

    /// <summary>Conversations the last sync wanted to revalidate, including those deferred by the budget.</summary>
    public int LastReconstructionCandidates => _reconstruction.ReconstructionCandidates;

    /// <summary>Conversation body fetches attempted by the last sync.</summary>
    public int LastBodyFetches => _reconstruction.BodyFetches;

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
        var coverage = new CoverageInfo();
        if (!options.BypassPause && IsPaused && PauseUntil is DateTimeOffset until && until > _clock.UtcNow)
        {
            return FailCurrentRun(coverage, AppSyncStatus.RateLimited, UiText.AutoSyncPaused, 0, options.Origin, log: false);
        }

        if (options.BypassPause)
        {
            IsPaused = false;
            PauseUntil = null;
            _consecutiveFailures = 0;
        }

        _log.Info(
            $"sync plan origin={options.Origin.ToString().ToLowerInvariant()} bypassPause={options.BypassPause} forceBodyRescan={options.ForceBodyRescan}");

        var parsed = 0;
        var worst = AppSyncStatus.UpToDate;
        _bodyFetchDelayMs = Math.Clamp(settings.BodyFetchDelayMilliseconds, 0, 5000);
        _pacedBodyThisSync = false;
        _consecutiveBodyTimeouts = 0;
        _reconstruction = new ReconstructionTotals();
        _reconstructionRevalidations = 0;
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
                return FailCurrentRun(coverage, AppSyncStatus.SignedOut, UiText.ChatGptSignedOut, parsed, options.Origin);
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
            var fetchedStatus = LastQuotaMetadata?.ProServerStatus ?? ProServerStatus.Unknown();
            if (options.KnownProServerStatus is not null)
            {
                fetchedStatus = fetchedStatus.Clone();
                ProServerStatus.RetainConfirmedReset(fetchedStatus, options.KnownProServerStatus);
                if (LastQuotaMetadata is not null)
                {
                    LastQuotaMetadata.ProServerStatus = fetchedStatus;
                }
            }

            var weeklyReset = LastQuotaMetadata?.WeeklyWindow(settings.PlanPreset)?.ResetAt;
            var period = ProQuotaPeriodResolver.Resolve(settings, periodReference, fetchedStatus, weeklyReset);
            var calculatedPeriodStart = period.Start;
            var periodStart = options.PeriodStartOverride ?? calculatedPeriodStart;
            var scanStart = periodStart;
            if (options.PeriodStartOverride is null)
            {
                var localWeekStart = QuotaPeriodCalculator.LocalWeekStart(periodReference, settings);
                if (localWeekStart < scanStart)
                {
                    scanStart = localWeekStart;
                }
            }

            var minUpdate = scanStart.ToUnixTimeSeconds();

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
                    coverage.PrimaryIndexSchemaMismatch = true;
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
            ApplyConversationSchemaHealth(coverage, value => worst = Worse(worst, value));
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
            try
            {
                var ledger = SummarizeLedger();
                _store.RecordMeteringSnapshot("sync-complete", new
                {
                    reconstructionVersion = ConversationFetchBackoff.ReconstructionSemanticsVersion,
                    capturedAt = completed.ToString("O", CultureInfo.InvariantCulture),
                    status = LastStatus.ToString(),
                    coverage = coverage.SummaryLabel,
                    fetched = _reconstruction.BodyFetches,
                    parsed = _reconstruction.WithEvents,
                    indexed = _reconstruction.IndexedConversations,
                    // Parsed-this-run family total. NOT the account's current-cycle count.
                    gptProParsedThisRun = FamilyCount(QuotaFamily.GptPro),
                    reconstructionCandidates = _reconstruction.ReconstructionCandidates,
                    reconstructionRevalidated = _reconstructionRevalidations,
                    mergedDuplicateCorrections = _reconstruction.MergedDuplicateCorrections,
                    canonicalCount = ledger.Canonical,
                    legacyPending = ledger.LegacyPending,
                    unresolvedCount = ledger.Unresolved
                }, completed);
            }
            catch (Exception ex)
            {
                _log.Warn("metering snapshot skipped: " + AppLog.Sanitize(ex.Message));
            }
            return new SyncOutcome(LastStatus, LastStatusDetail, parsed);
        }
        catch (ChatGptProviderException ex) when (ex.IsUnauthorized)
        {
            return FailCurrentRun(coverage, AppSyncStatus.AuthenticationRequired, UiText.ChatGptSessionExpired, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsForbidden)
        {
            return FailCurrentRun(coverage, AppSyncStatus.Forbidden, CompanionDiagnostics.Forbidden403, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsChatGptTabRequired)
        {
            return FailCurrentRun(coverage, AppSyncStatus.ChatGptTabRequired, CompanionDiagnostics.NoChatGptTab, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsPageBridgeUnavailable)
        {
            return FailCurrentRun(coverage, AppSyncStatus.PageBridgeUnavailable, CompanionDiagnostics.PageBridgeUnavailable, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.SchemaMismatch)
        {
            return FailCurrentRun(coverage, AppSyncStatus.ProviderSchemaMismatch, UiText.SchemaMismatchStatus, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsRateLimited)
        {
            Pause(TimeSpan.FromMinutes(20));
            return FailCurrentRun(coverage, AppSyncStatus.RateLimited, UiText.RateLimitedPaused, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsCompanionDisconnected)
        {
            return FailCurrentRun(coverage, AppSyncStatus.CompanionDisconnected, UiText.CompanionDisconnectedStatus, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsBridgeTimeout)
        {
            return FailCurrentRun(coverage, AppSyncStatus.BridgeTimeout, UiText.BridgeTimeoutStatus, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsBridgeWriteFailed)
        {
            return FailCurrentRun(coverage, AppSyncStatus.BridgeWriteFailed, UiText.BridgeWriteFailedStatus, parsed, options.Origin);
        }
        catch (ChatGptProviderException ex) when (ex.IsOffline)
        {
            return FailCurrentRun(coverage, AppSyncStatus.Offline, UiText.ChatGptUnreachable, parsed, options.Origin);
        }
        catch (OperationCanceledException)
        {
            return FailCurrentRun(coverage, AppSyncStatus.Error, "cancelled", parsed, options.Origin, log: false);
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= 3)
            {
                Pause(TimeSpan.FromMinutes(30));
            }

            return FailCurrentRun(coverage, AppSyncStatus.Error, AppLog.Sanitize(ex.Message), parsed, options.Origin);
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
            coverage.PrimaryIndexSchemaMismatch = true;
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
            coverage.PrimaryIndexSchemaMismatch = true;
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
        var changed = items.Count(item => DecideBodyFetch(item, options) != BodyFetchReason.None);
        _log.Info($"{source} changed={changed} forceBodyRescan={options.ForceBodyRescan} bypassPause={options.BypassPause}");
        _reconstruction.IndexedConversations += items.Count;
        var parsed = 0;
        var processed = 0;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decision = DecideBodyFetch(item, options);
            if (_work.TryGetValue(item.Id, out var existing))
            {
                if (decision != BodyFetchReason.None)
                {
                    _reconstruction.ScanAttempts++;
                }

                MergeAppearance(existing, item);
                continue;
            }

            if (decision == BodyFetchReason.None)
            {
                var record = _store.GetConversation(item.Id);
                if (ConversationFetchBackoff.IsDeferredFailure(item, record, _clock.UtcNow, false))
                {
                    MarkDeferred(coverage, item.Id, record, setWorst);
                }

                continue;
            }

            if (decision == BodyFetchReason.ReconstructionRevalidation)
            {
                _reconstruction.ReconstructionCandidates++;
                if (_reconstructionRevalidations >= ConversationFetchBackoff.MaxReconstructionRevalidationsPerSync)
                {
                    // Budget exhausted. Leave ReconstructionVersion old so a later sync retries it.
                    continue;
                }

                _reconstructionRevalidations++;
            }

            _reconstruction.ScanAttempts++;
            _reconstruction.UniqueConversations++;
            var entry = new ConversationWorkEntry { First = item, UsageSource = usageSource, Attempted = true };
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
                        bodyCts.Token,
                        cancellationToken);
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
                    var detail = AppLog.Sanitize(ex.Message);
                    MarkUniqueFailure(coverage, entry, setWorst, AppSyncStatus.PartialData, category, status, detail);
                    RecordBodyFailure(item, ConversationScanStatus.FetchFailed, detail, category, status);
                    LogConversationFailure(item.Id, category, status, detail);
                    continue;
                }

                if (!load.Complete || load.SchemaMismatch || load.Conversation is null)
                {
                    var category = SyncFailureClassifier.ClassifyLoad(load);
                    var schema = string.Equals(category, ConversationFetchBackoff.SchemaMismatch, StringComparison.Ordinal);
                    var scanStatus = schema ? ConversationScanStatus.SchemaMismatch : ConversationScanStatus.FetchFailed;
                    var detail = string.Join("; ", load.Diagnostics);
                    MarkUniqueFailure(coverage, entry, setWorst, AppSyncStatus.PartialData, category, 0, detail);
                    coverage.Notes = schema
                        ? "Provider schema mismatch on at least one conversation."
                        : "At least one conversation was incomplete.";
                    RecordBodyFailure(item, scanStatus, detail, category);
                    LogConversationFailure(item.Id, category, 0, detail);
                    continue;
                }

                var result = _parser.Parse(load.Conversation, new ConversationParseContext
                {
                    ConversationId = item.Id,
                    ProjectId = item.ProjectId,
                    Archived = item.Archived,
                    Source = usageSource,
                    UpdateTime = item.UpdateTime
                });

                if (result.SchemaMismatch)
                {
                    MarkUniqueFailure(coverage, entry, setWorst, AppSyncStatus.PartialData, ConversationFetchBackoff.SchemaMismatch);
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
        int httpStatus = 0,
        string? detail = null)
    {
        if (!entry.Failed)
        {
            entry.Failed = true;
            entry.FailureCategory = category;
            _reconstruction.Failed++;
            coverage.FailedConversations++;
            coverage.FailureSummary.AddThisSync(category, httpStatus, detail);
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
        coverage.FailureSummary.AddDeferred(existing?.LastFetchFailureCategory, existing?.LastError);
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

    private void LogConversationFailure(string conversationId, string category, int status, string? reason = null)
    {
        var suffix = string.IsNullOrWhiteSpace(reason) ? "" : $" reason={AppLog.Sanitize(reason)}";
        _log.Warn($"conversation fetch failed id={conversationId} category={category} status={status}{suffix}");
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
        var outcome = _store.ReconcileConversation(new ConversationRecord
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
            FetchFailureParserVersion = 0,
            ReconstructionVersion = ConversationFetchBackoff.ReconstructionSemanticsVersion
        }, result.Events, result.Observations, _ledger);
        _reconstruction.MergedDuplicateCorrections += outcome.MergedDuplicateCorrections;
        if (!trusted)
        {
            coverage.ConversationIncomplete = true;
            setWorst(AppSyncStatus.PartialData);
        }

        return result.Events.Count;
    }

    private SyncOutcome FailCurrentRun(
        CoverageInfo coverage,
        AppSyncStatus status,
        string? detail,
        int parsed,
        SyncOrigin origin,
        bool log = true)
    {
        LastCoverage = coverage;
        LastStatus = status;
        LastStatusDetail = detail;
        if (log)
        {
            LogSyncFailure(origin, status);
        }

        return new SyncOutcome(status, detail, parsed);
    }

    private bool RestoreConversationSchemaLatch() =>
        ConversationSchemaHealthPolicy.RestoreLatch(
            _store.GetState(ConversationSchemaHealthPolicy.LatchStateKey),
            _store.GetState(ConversationSchemaHealthPolicy.LatchParserVersionStateKey),
            ConversationFetchBackoff.ParserCompatibilityVersion);

    private void PersistConversationSchemaLatch(bool latched)
    {
        _store.SetState(ConversationSchemaHealthPolicy.LatchStateKey, latched ? "true" : "false");
        _store.SetState(
            ConversationSchemaHealthPolicy.LatchParserVersionStateKey,
            ConversationFetchBackoff.ParserCompatibilityVersion.ToString(CultureInfo.InvariantCulture));
    }

    private void ApplyConversationSchemaHealth(CoverageInfo coverage, Action<AppSyncStatus> setWorst)
    {
        var assessment = ConversationSchemaHealthPolicy.Evaluate(CollectConversationSchemaEvidence());
        var next = ConversationSchemaHealthPolicy.NextLatch(ConversationSchemaSystemicFailureLatched, assessment);
        ConversationSchemaSystemicFailureLatched = next;
        PersistConversationSchemaLatch(next);
        coverage.ConversationSchemaSystemicFailure = next;
        if (!next)
        {
            return;
        }

        setWorst(AppSyncStatus.ProviderSchemaMismatch);
        coverage.Notes = "Systemic conversation schema mismatch.";
    }

    private ConversationSchemaHealthEvidence CollectConversationSchemaEvidence()
    {
        var attempted = 0;
        var successful = 0;
        var schema = 0;
        var timeout = 0;
        var other = 0;
        foreach (var entry in _work.Values)
        {
            if (!entry.Attempted)
            {
                continue;
            }

            attempted++;
            if (entry.Succeeded || entry.DeferredZero)
            {
                successful++;
                continue;
            }

            switch (ConversationFetchBackoff.NormalizeCategory(entry.FailureCategory))
            {
                case ConversationFetchBackoff.SchemaMismatch:
                    schema++;
                    break;
                case ConversationFetchBackoff.BodyTimeout:
                    timeout++;
                    break;
                default:
                    other++;
                    break;
            }
        }

        return new ConversationSchemaHealthEvidence(attempted, successful, schema, timeout, other);
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

    /// <summary>Account-wide aggregate ledger state. Safe counts only, no conversation identifiers.</summary>
    private (int Canonical, int LegacyPending, int Unresolved) SummarizeLedger()
    {
        var stored = _store.GetUsageEvents();
        var legacy = stored.Count(QuotaEngine.IsLegacyUnverified);
        var unresolved = stored.Count(e =>
            !QuotaEngine.IsLegacyUnverified(e) && e.UnresolvedKind != UnresolvedEvidenceKind.None);
        return (stored.Count - legacy, legacy, unresolved);
    }

    private BodyFetchReason DecideBodyFetch(ConversationIndexItem item, SyncRunOptions options) =>
        ConversationFetchBackoff.DecideBodyFetch(
            item,
            _store.GetConversation(item.Id),
            _clock.UtcNow,
            options.ForceBodyRescan);

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

    private async Task<T> ExecuteWithRetry<T>(
        Func<Task<T>> action,
        string operation,
        CancellationToken cancellationToken,
        CancellationToken? realCancellationToken = null)
    {
        var abortToken = realCancellationToken ?? cancellationToken;
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
                    try
                    {
                        await Task.Delay(wait, cancellationToken);
                    }
                    catch (OperationCanceledException) when (!abortToken.IsCancellationRequested)
                    {
                        // The per-conversation body budget expired while we were waiting
                        // out a rate limit/server error, not because of a real cancellation.
                        // Surface the original condition so the caller aborts the whole
                        // sync (AGENTS.md: fatal 429/5xx must abort, not become a
                        // per-conversation BodyTimeout that hides sustained throttling).
                        throw ex;
                    }
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
        public int ReconstructionCandidates { get; set; }
        public int MergedDuplicateCorrections { get; set; }
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
        public bool Attempted { get; set; }
        public bool BodyFetched { get; set; }
        public bool Failed { get; set; }
        public bool Succeeded { get; set; }
        public bool DeferredZero { get; set; }
        public string? FailureCategory { get; set; }
    }
}

public sealed record SyncOutcome(AppSyncStatus Status, string? Detail, int ParsedEvents);

public sealed record SyncRunOptions
{
    public DateTimeOffset? PeriodStartOverride { get; init; }
    public ProServerStatus? KnownProServerStatus { get; init; }
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
