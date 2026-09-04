namespace ProMeter.Services;

public enum WebViewVerificationStatus
{
    Passed,
    CountsDiffer,
    IncompleteBaseline,
    IncompleteWebView,
    Failed,
    Cancelled
}

public enum VerificationDifferenceSide
{
    BrowserCompanionOnly,
    WebViewOnly
}

public sealed record WebViewVerificationDifference(
    VerificationDifferenceSide Side,
    string ConversationId,
    string RawModel,
    DateTimeOffset CreatedAt);

public sealed record WebViewVerificationBaseline(
    AuthTransportKind SourceTransport,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    int Count,
    bool Complete,
    bool Estimated,
    IReadOnlyList<UsageEvent> PeriodEvents)
{
    public bool IsBrowserCompanion => SourceTransport == AuthTransportKind.BrowserCompanion;

    public static WebViewVerificationBaseline Capture(
        IReadOnlyList<UsageEvent> productionEvents,
        AppSettings settings,
        AuthTransportKind sourceTransport,
        CoverageInfo coverage,
        AppSyncStatus status,
        QuotaMetadataSet? quotaMetadata,
        DateTimeOffset now)
    {
        var serverReset = quotaMetadata?.WeeklyWindow(settings.PlanPreset)?.ResetAt;
        var period = QuotaPeriodCalculator.CurrentPeriod(settings, now, serverReset);
        var usedServerBoundary = serverReset is not null
            && now >= period.Start
            && now < period.End;
        if (now < period.Start || now >= period.End)
        {
            period = QuotaPeriodCalculator.CurrentPeriod(settings, now);
            usedServerBoundary = false;
        }

        var boundaryTrusted = usedServerBoundary || settings.ResetAnchorConfigured;
        var scanComplete = WebViewVerificationRules.IsScanComplete(status, coverage);
        var confidenceComplete = coverage.CountConfidence != CoverageConfidence.Incomplete
            && coverage.ResetConfidence is CoverageConfidence.Authoritative or CoverageConfidence.HighConfidence;
        var periodEvents = WebViewVerificationRules.RelevantEvents(
                productionEvents,
                settings.PlanPreset,
                period.Start,
                period.End)
            .Select(CloneEvent)
            .ToList();
        var estimated = !boundaryTrusted
            || coverage.CountConfidence is CoverageConfidence.Estimated or CoverageConfidence.Incomplete
            || coverage.ResetConfidence is CoverageConfidence.Estimated or CoverageConfidence.Incomplete;

        return new WebViewVerificationBaseline(
            sourceTransport,
            period.Start,
            period.End,
            periodEvents.Count,
            sourceTransport == AuthTransportKind.BrowserCompanion && scanComplete && boundaryTrusted && confidenceComplete,
            estimated,
            periodEvents);
    }

    private static UsageEvent CloneEvent(UsageEvent source) => new()
    {
        Id = source.Id,
        RequestId = source.RequestId,
        ConversationId = source.ConversationId,
        MessageId = source.MessageId,
        CreatedAt = source.CreatedAt,
        RequestedModel = source.RequestedModel,
        ResponseModel = source.ResponseModel,
        NormalizedModel = source.NormalizedModel,
        RawModel = source.RawModel,
        ReasoningEffort = source.ReasoningEffort,
        Source = source.Source,
        ProjectId = source.ProjectId,
        IsArchived = source.IsArchived,
        FirstSeenAt = source.FirstSeenAt,
        LastSeenAt = source.LastSeenAt,
        QuotaFamily = source.QuotaFamily,
        DedupeKey = source.DedupeKey,
        DedupeConfidence = source.DedupeConfidence
    };
}

public sealed record WebViewVerificationResult(
    WebViewVerificationStatus Status,
    int BrowserCompanionCount,
    int? WebViewCount,
    int? Difference,
    bool BrowserCompanionEstimated,
    bool UsedIsolatedStore,
    string? TechnicalDetail,
    IReadOnlyList<WebViewVerificationDifference> MetadataDifferences)
{
    public bool CanUseWebViewAsDefault => Status == WebViewVerificationStatus.Passed;
}

public static class WebViewVerificationRules
{
    public static bool IsScanComplete(AppSyncStatus status, CoverageInfo coverage) =>
        status == AppSyncStatus.UpToDate
        && coverage.NormalChats
        && coverage.ArchivedChats
        && coverage.Projects
        && coverage.NormalIndexState == CollectionState.Complete
        && coverage.ArchivedIndexState == CollectionState.Complete
        && coverage.ProjectsIndexState == CollectionState.Complete
        && !coverage.IndexIncomplete
        && !coverage.ConversationIncomplete
        && coverage.FailedConversations == 0
        && !coverage.HistoryLoadedWithoutUsage;

    public static IReadOnlyList<UsageEvent> RelevantEvents(
        IEnumerable<UsageEvent> events,
        SubscriptionPreset preset,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd) =>
        events.Where(item =>
                QuotaPeriodCalculator.InRange(item.CreatedAt, periodStart, periodEnd)
                && item.QuotaFamily == QuotaFamily.GptPro
                && (preset != SubscriptionPreset.Pro200 || QuotaEngine.IsGpt6Pro(item)))
            .ToList();

    public static IReadOnlyList<WebViewVerificationDifference> CompareMetadata(
        IReadOnlyList<UsageEvent> baseline,
        IReadOnlyList<UsageEvent> webView,
        int limit = 25)
    {
        var baselineByKey = baseline.GroupBy(Key).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var webViewByKey = webView.GroupBy(Key).ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var differences = new List<WebViewVerificationDifference>();
        foreach (var key in baselineByKey.Keys.Union(webViewByKey.Keys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            baselineByKey.TryGetValue(key, out var left);
            webViewByKey.TryGetValue(key, out var right);
            left ??= [];
            right ??= [];
            for (var i = right.Count; i < left.Count && differences.Count < limit; i++)
            {
                differences.Add(ToDifference(VerificationDifferenceSide.BrowserCompanionOnly, left[i]));
            }

            for (var i = left.Count; i < right.Count && differences.Count < limit; i++)
            {
                differences.Add(ToDifference(VerificationDifferenceSide.WebViewOnly, right[i]));
            }

            if (differences.Count >= limit)
            {
                break;
            }
        }

        return differences;
    }

    private static string Key(UsageEvent item) =>
        string.IsNullOrWhiteSpace(item.DedupeKey)
            ? UsageEvent.BuildDedupeKey(item.ConversationId, item.RequestId, item.MessageId)
            : item.DedupeKey;

    private static WebViewVerificationDifference ToDifference(VerificationDifferenceSide side, UsageEvent item) =>
        new(side, item.ConversationId, item.RawModel, item.CreatedAt);
}

/// <summary>
/// Runs the standard reconstruction pipeline against a throwaway database and
/// compares only an immutable production snapshot supplied by the caller.
/// </summary>
public sealed class WebViewFullVerificationService
{
    private readonly AppLog _log;
    private readonly IClock _clock;
    private readonly string _temporaryBaseDirectory;

    public WebViewFullVerificationService(AppLog log, IClock? clock = null, string? temporaryBaseDirectory = null)
    {
        _log = log;
        _clock = clock ?? SystemClock.Instance;
        _temporaryBaseDirectory = temporaryBaseDirectory
            ?? Path.Combine(Path.GetTempPath(), "ProMeter", "webview-verification");
    }

    public async Task<WebViewVerificationResult> RunAsync(
        IChatGptProvider webViewProvider,
        AppSettings settings,
        WebViewVerificationBaseline baseline,
        CancellationToken cancellationToken = default)
    {
        if (!baseline.IsBrowserCompanion || !baseline.Complete)
        {
            return new WebViewVerificationResult(
                WebViewVerificationStatus.IncompleteBaseline,
                baseline.Count,
                null,
                null,
                baseline.Estimated,
                UsedIsolatedStore: false,
                TechnicalDetail: UiText.WebViewBaselineTechnical(baseline.IsBrowserCompanion),
                MetadataDifferences: []);
        }

        var runDirectory = Path.GetFullPath(Path.Combine(_temporaryBaseDirectory, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(runDirectory);
        var databasePath = Path.Combine(runDirectory, "verification.db");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var store = new SqliteStore(databasePath, pooling: false);
            var models = new ModelNormalizer();
            var parser = new ConversationParser(models);
            var isolatedSettings = CloneSettings(settings);
            var sync = new SyncEngine(store, parser, models, _log, _clock);
            var outcome = await sync.SyncAsync(
                webViewProvider,
                isolatedSettings,
                force: true,
                new SyncRunOptions { PeriodStartOverride = baseline.PeriodStart },
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var webViewEvents = WebViewVerificationRules.RelevantEvents(
                store.GetUsageEvents(),
                isolatedSettings.PlanPreset,
                baseline.PeriodStart,
                baseline.PeriodEnd);
            var webViewCount = webViewEvents.Count;
            var difference = webViewCount - baseline.Count;
            var metadataDifferences = WebViewVerificationRules.CompareMetadata(baseline.PeriodEvents, webViewEvents);
            if (!WebViewVerificationRules.IsScanComplete(outcome.Status, sync.LastCoverage))
            {
                return new WebViewVerificationResult(
                    WebViewVerificationStatus.IncompleteWebView,
                    baseline.Count,
                    webViewCount,
                    difference,
                    baseline.Estimated,
                    UsedIsolatedStore: true,
                    TechnicalDetail: UiText.WebViewVerificationStatusTechnical(outcome.Status),
                    MetadataDifferences: metadataDifferences);
            }

            return new WebViewVerificationResult(
                difference == 0 ? WebViewVerificationStatus.Passed : WebViewVerificationStatus.CountsDiffer,
                baseline.Count,
                webViewCount,
                difference,
                baseline.Estimated,
                UsedIsolatedStore: true,
                TechnicalDetail: difference == 0 ? null : UiText.WebViewVerificationCountsDifferTechnical,
                MetadataDifferences: metadataDifferences);
        }
        catch (OperationCanceledException)
        {
            return new WebViewVerificationResult(
                WebViewVerificationStatus.Cancelled,
                baseline.Count,
                null,
                null,
                baseline.Estimated,
                UsedIsolatedStore: true,
                TechnicalDetail: null,
                MetadataDifferences: []);
        }
        catch
        {
            return new WebViewVerificationResult(
                WebViewVerificationStatus.Failed,
                baseline.Count,
                null,
                null,
                baseline.Estimated,
                UsedIsolatedStore: true,
                TechnicalDetail: UiText.WebViewVerificationUnavailableTechnical,
                MetadataDifferences: []);
        }
        finally
        {
            TryDeleteRunDirectory(runDirectory);
        }
    }

    private static AppSettings CloneSettings(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))
        ?? throw new InvalidOperationException("Could not create isolated settings snapshot.");

    private void TryDeleteRunDirectory(string runDirectory)
    {
        try
        {
            if (Directory.Exists(runDirectory))
            {
                Directory.Delete(runDirectory, recursive: true);
            }
        }
        catch
        {
            // A locked temporary WAL may remain until process exit. Never fall
            // back to a broader path or touch the production database.
            _log.Warn("webview verification temporary database cleanup failed");
        }
    }
}

public static class WebViewDefaultConnectionSelection
{
    public static bool TryApply(
        AppSettings settings,
        WebViewVerificationResult verification,
        bool explicitlyConfirmed)
    {
        if (!explicitlyConfirmed || !verification.CanUseWebViewAsDefault)
        {
            return false;
        }

        settings.AuthTransport = AuthTransportKind.WebView2;
        return true;
    }
}
