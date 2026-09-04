using System.Text.Json;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class WebViewVerificationTests
{
    [Fact]
    public async Task FullVerification_UsesIsolatedStoreAndLeavesProductionDatabaseUntouched()
    {
        var testDirectory = NewTempDirectory();
        var productionPath = Path.Combine(testDirectory, "production.db");
        using var production = new SqliteStore(productionPath);
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var eventTime = now.AddMinutes(-1);
        var officialExport = Usage("official-export", "req-export", now.AddDays(-30));
        officialExport.Source = UsageSource.OfficialExport;
        production.UpsertUsageEvents([Usage("conv-base", "req-base", eventTime), officialExport]);
        production.SetState("last_index_sync", "production-watermark");
        var settings = CompleteSettings();
        var baseline = CaptureBaseline(production.GetUsageEvents(), settings, now);
        var eventsBefore = JsonSerializer.Serialize(production.GetUsageEvents());
        var conversationsBefore = JsonSerializer.Serialize(production.GetConversations());
        var settingsBefore = JsonSerializer.Serialize(settings);
        var provider = ProviderWithEvents(("conv-base", "req-base", eventTime));
        var log = new AppLog(Path.Combine(testDirectory, "logs"));
        var temporaryBase = Path.Combine(testDirectory, "isolated");
        var service = new WebViewFullVerificationService(log, new MutableClock(now), temporaryBase);

        var result = await service.RunAsync(provider, settings, baseline);

        Assert.Equal(WebViewVerificationStatus.Passed, result.Status);
        Assert.True(result.UsedIsolatedStore);
        Assert.Equal(eventsBefore, JsonSerializer.Serialize(production.GetUsageEvents()));
        Assert.Equal(conversationsBefore, JsonSerializer.Serialize(production.GetConversations()));
        Assert.Equal("production-watermark", production.GetState("last_index_sync"));
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
        Assert.Empty(Directory.EnumerateDirectories(temporaryBase));
    }

    [Fact]
    public async Task EqualCounts_ReportDifferenceZero()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        var eventTime = now.AddMinutes(-1);
        var baseline = CaptureBaseline([Usage("conv-base", "req-base", eventTime)], settings, now);

        var result = await CreateService(now).RunAsync(
            ProviderWithEvents(("conv-base", "req-base", eventTime)),
            settings,
            baseline);

        Assert.Equal(WebViewVerificationStatus.Passed, result.Status);
        Assert.Equal(1, result.BrowserCompanionCount);
        Assert.Equal(1, result.WebViewCount);
        Assert.Equal(0, result.Difference);
        Assert.True(result.CanUseWebViewAsDefault);
    }

    [Fact]
    public async Task UnequalCounts_ReportSignedDifferenceAndMetadataOnlyDetails()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        var first = now.AddMinutes(-2);
        var second = now.AddMinutes(-1);
        var baseline = CaptureBaseline([Usage("conv-base", "req-base", first)], settings, now);

        var result = await CreateService(now).RunAsync(
            ProviderWithEvents(
                ("conv-base", "req-base", first),
                ("conv-web-only", "req-web-only", second)),
            settings,
            baseline);

        Assert.Equal(WebViewVerificationStatus.CountsDiffer, result.Status);
        Assert.Equal(2, result.WebViewCount);
        Assert.Equal(1, result.Difference);
        Assert.False(result.CanUseWebViewAsDefault);
        var extra = Assert.Single(result.MetadataDifferences);
        Assert.Equal(VerificationDifferenceSide.WebViewOnly, extra.Side);
        Assert.Equal("conv-web-only", extra.ConversationId);
        Assert.Equal("gpt-5-6-pro", extra.RawModel);
        var serialized = JsonSerializer.Serialize(result.MetadataDifferences);
        Assert.DoesNotContain("title", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assistant", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IncompleteBrowserCompanionBaseline_DoesNotProduceFalsePass()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        var coverage = CompleteCoverage();
        coverage.IndexIncomplete = true;
        var baseline = WebViewVerificationBaseline.Capture(
            [],
            settings,
            AuthTransportKind.BrowserCompanion,
            coverage,
            AppSyncStatus.PartialData,
            null,
            now);

        var result = await CreateService(now).RunAsync(new FixtureChatGptProvider(), settings, baseline);

        Assert.Equal(WebViewVerificationStatus.IncompleteBaseline, result.Status);
        Assert.False(result.UsedIsolatedStore);
        Assert.False(result.CanUseWebViewAsDefault);
        Assert.Null(result.Difference);
    }

    [Fact]
    public async Task UnconfirmedQuotaBoundary_DoesNotProduceFalsePass()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        settings.ResetAnchorConfigured = false;
        var coverage = CompleteCoverage();
        coverage.CountConfidence = CoverageConfidence.Estimated;
        coverage.ResetConfidence = CoverageConfidence.Estimated;
        var baseline = WebViewVerificationBaseline.Capture(
            [],
            settings,
            AuthTransportKind.BrowserCompanion,
            coverage,
            AppSyncStatus.UpToDate,
            null,
            now);

        var result = await CreateService(now).RunAsync(new FixtureChatGptProvider(), settings, baseline);

        Assert.True(baseline.Estimated);
        Assert.False(baseline.Complete);
        Assert.Equal(WebViewVerificationStatus.IncompleteBaseline, result.Status);
    }

    [Fact]
    public void StaleServerReset_IsNotUsedAsTheComparisonPeriod()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        var quota = new QuotaMetadataSet
        {
            SharedProWeekly = new QuotaWindow
            {
                Found = true,
                ResetAt = now.AddDays(-21),
                Used = 1,
                Limit = 50
            }
        };
        var current = Usage("conv-current", "req-current", now.AddMinutes(-1));

        var baseline = WebViewVerificationBaseline.Capture(
            [current],
            settings,
            AuthTransportKind.BrowserCompanion,
            CompleteCoverage(),
            AppSyncStatus.UpToDate,
            quota,
            now);

        Assert.True(now >= baseline.PeriodStart && now < baseline.PeriodEnd);
        Assert.Equal(1, baseline.Count);
        Assert.True(baseline.Complete);
    }

    [Fact]
    public void EstimatedCoverageBoundary_DoesNotBecomeEligibleFromSettingsAlone()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        var coverage = CompleteCoverage();
        coverage.ResetConfidence = CoverageConfidence.Estimated;
        coverage.ResetAnchorSource = ResetAnchorSource.Default;

        var baseline = WebViewVerificationBaseline.Capture(
            [],
            settings,
            AuthTransportKind.BrowserCompanion,
            coverage,
            AppSyncStatus.UpToDate,
            null,
            now);

        Assert.False(baseline.Complete);
        Assert.True(baseline.Estimated);
    }

    [Fact]
    public void Pro200Comparison_UsesOnlyTheWeeklyGpt6PeriodCount()
    {
        var start = new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero);
        var gpt6 = Usage("gpt6", "req-gpt6", start.AddDays(1));
        gpt6.RawModel = "gpt-6-pro";
        gpt6.NormalizedModel = "GPT-6 Pro";
        var sol = Usage("sol", "req-sol", start.AddDays(1));

        var relevant = WebViewVerificationRules.RelevantEvents(
            [gpt6, sol],
            SubscriptionPreset.Pro200,
            start,
            start.AddDays(7));

        var only = Assert.Single(relevant);
        Assert.Equal("gpt6", only.ConversationId);
    }

    [Fact]
    public async Task IncompleteWebViewReconstruction_DoesNotProduceFalsePass()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        var eventTime = now.AddMinutes(-1);
        var baseline = CaptureBaseline([Usage("conv-base", "req-base", eventTime)], settings, now);
        var provider = ProviderWithEvents(("conv-base", "req-base", eventTime));
        provider.IndexIncomplete = true;

        var result = await CreateService(now).RunAsync(provider, settings, baseline);

        Assert.Equal(WebViewVerificationStatus.IncompleteWebView, result.Status);
        Assert.Equal(0, result.Difference);
        Assert.False(result.CanUseWebViewAsDefault);
    }

    [Fact]
    public async Task SelectedTransportAutoSyncAndStartupRemainUnchangedAfterVerification()
    {
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        settings.AuthTransport = AuthTransportKind.BrowserCompanion;
        settings.AutoSync = false;
        settings.StartWithWindows = false;
        var eventTime = now.AddMinutes(-1);
        var baseline = CaptureBaseline([Usage("conv-base", "req-base", eventTime)], settings, now);

        await CreateService(now).RunAsync(
            ProviderWithEvents(("conv-base", "req-base", eventTime)),
            settings,
            baseline);

        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
        Assert.False(settings.AutoSync);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public void ExplicitConfirmationAndPassingVerification_AreBothRequiredToChangeTransport()
    {
        var settings = CompleteSettings();
        var passing = Result(WebViewVerificationStatus.Passed);
        var differing = Result(WebViewVerificationStatus.CountsDiffer);

        Assert.False(WebViewDefaultConnectionSelection.TryApply(settings, passing, explicitlyConfirmed: false));
        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
        Assert.False(WebViewDefaultConnectionSelection.TryApply(settings, differing, explicitlyConfirmed: true));
        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
        Assert.True(WebViewDefaultConnectionSelection.TryApply(settings, passing, explicitlyConfirmed: true));
        Assert.Equal(AuthTransportKind.WebView2, settings.AuthTransport);
        Assert.False(settings.AutoSync);
        Assert.False(settings.StartWithWindows);
    }

    [Fact]
    public async Task PromptAssistantTokenCookieAndAuthorizationValues_NeverReachLogsOrResult()
    {
        const string secret = "verification-secret-value";
        var now = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var settings = CompleteSettings();
        var eventTime = now.AddMinutes(-1);
        var baseline = CaptureBaseline([Usage("conv-base", "req-base", eventTime)], settings, now);
        var directory = NewTempDirectory();
        var log = new AppLog(Path.Combine(directory, "logs"));
        var service = new WebViewFullVerificationService(log, new MutableClock(now), Path.Combine(directory, "isolated"));
        var provider = new ThrowingVerificationProvider($"Authorization: Bearer {secret}; Cookie={secret}; prompt={secret}; assistant={secret}");

        var result = await service.RunAsync(provider, settings, baseline);
        var resultJson = JsonSerializer.Serialize(result);
        var logText = string.Join(Environment.NewLine, Directory.GetFiles(log.DirectoryPath, "*.log").Select(File.ReadAllText));

        Assert.Equal(WebViewVerificationStatus.IncompleteWebView, result.Status);
        Assert.DoesNotContain(secret, resultJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, logText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer " + secret, logText, StringComparison.Ordinal);
    }

    private static WebViewFullVerificationService CreateService(DateTimeOffset now)
    {
        var directory = NewTempDirectory();
        return new WebViewFullVerificationService(
            new AppLog(Path.Combine(directory, "logs")),
            new MutableClock(now),
            Path.Combine(directory, "isolated"));
    }

    private static WebViewVerificationBaseline CaptureBaseline(
        IReadOnlyList<UsageEvent> events,
        AppSettings settings,
        DateTimeOffset now) =>
        WebViewVerificationBaseline.Capture(
            events,
            settings,
            AuthTransportKind.BrowserCompanion,
            CompleteCoverage(),
            AppSyncStatus.UpToDate,
            null,
            now);

    private static AppSettings CompleteSettings() => new()
    {
        PlanPreset = SubscriptionPreset.Pro100,
        WeeklyProQuota = 50,
        ResetWeekday = DayOfWeek.Monday,
        ResetTime = TimeSpan.Zero,
        ResetTimeZoneId = "UTC",
        ResetAnchorConfigured = true,
        AuthTransport = AuthTransportKind.BrowserCompanion,
        AutoSync = false,
        StartWithWindows = false,
        BodyFetchDelayMilliseconds = 0
    };

    private static CoverageInfo CompleteCoverage() => new()
    {
        NormalChats = true,
        ArchivedChats = true,
        Projects = true,
        NormalIndexState = CollectionState.Complete,
        ArchivedIndexState = CollectionState.Complete,
        ProjectsIndexState = CollectionState.Complete,
        CountConfidence = CoverageConfidence.HighConfidence,
        ResetConfidence = CoverageConfidence.HighConfidence,
        ResetAnchorSource = ResetAnchorSource.UserConfigured
    };

    private static FixtureChatGptProvider ProviderWithEvents(params (string ConversationId, string RequestId, DateTimeOffset CreatedAt)[] items)
    {
        var provider = new FixtureChatGptProvider();
        foreach (var item in items)
        {
            var update = item.CreatedAt.AddSeconds(1).ToUnixTimeSeconds();
            var body = ConversationFixtures.NormalPro(item.ConversationId, update);
            var metadata = body["mapping"]?["asst-1"]?["message"]?["metadata"] as JsonObject;
            Assert.NotNull(metadata);
            metadata["request_id"] = item.RequestId;
            provider.AddConversation(new ConversationIndexItem
            {
                Id = item.ConversationId,
                CreateTime = update - 10,
                UpdateTime = update,
                Source = "chat"
            }, body);
        }

        return provider;
    }

    private static UsageEvent Usage(string conversationId, string requestId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ConversationId = conversationId,
        RequestId = requestId,
        MessageId = "assistant-" + conversationId,
        CreatedAt = createdAt,
        FirstSeenAt = createdAt,
        LastSeenAt = createdAt,
        RawModel = "gpt-5-6-pro",
        NormalizedModel = "GPT-5.6 Sol Pro",
        QuotaFamily = QuotaFamily.GptPro,
        DedupeKey = "req:" + requestId
    };

    private static WebViewVerificationResult Result(WebViewVerificationStatus status) => new(
        status,
        BrowserCompanionCount: 1,
        WebViewCount: 1,
        Difference: 0,
        BrowserCompanionEstimated: false,
        UsedIsolatedStore: true,
        TechnicalDetail: null,
        MetadataDifferences: []);

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class ThrowingVerificationProvider : IChatGptProvider
    {
        private readonly string _message;

        public ThrowingVerificationProvider(string message) => _message = message;

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(_message);

        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
