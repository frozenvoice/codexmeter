using ProMeter.Codex;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class SyncFailureNotificationTests
{
    [Fact]
    public async Task OfflineFailure_WritesSafeLogEvent()
    {
        var dir = NewTempDir();
        var logs = Path.Combine(dir, "logs");
        var (engine, provider, settings) = CreateOfflineHarness(dir, logs, _ =>
            throw new ChatGptProviderException("chatgpt.com proxy 10.0.0.1 session=secret", 0));
        var outcome = await engine.SyncAsync(
            provider,
            settings,
            true,
            new SyncRunOptions { Origin = SyncOrigin.Auto });
        Assert.Equal(AppSyncStatus.Offline, outcome.Status);
        var text = ReadLogs(logs);
        Assert.Contains("sync failed origin=auto category=Offline", text, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("session=secret", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SignedOutFailure_WritesSafeLogEvent()
    {
        var dir = NewTempDir();
        var logs = Path.Combine(dir, "logs");
        using var store = new SqliteStore(Path.Combine(dir, "signed-out.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(logs));
        var outcome = await engine.SyncAsync(
            new FixtureChatGptProvider(new AccountStatus { IsSignedIn = false }),
            AppSettings.CreateDefaults(),
            true,
            new SyncRunOptions { Origin = SyncOrigin.Auto });
        Assert.Equal(AppSyncStatus.SignedOut, outcome.Status);
        Assert.Contains("sync failed origin=auto category=SignedOut", ReadLogs(logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenericError_LogsCategoryWithoutExceptionText()
    {
        var dir = NewTempDir();
        var logs = Path.Combine(dir, "logs");
        using var store = new SqliteStore(Path.Combine(dir, "error.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(logs));
        var outcome = await engine.SyncAsync(
            new ThrowingAccountProvider(),
            AppSettings.CreateDefaults(),
            true,
            new SyncRunOptions { Origin = SyncOrigin.Manual });
        Assert.Equal(AppSyncStatus.Error, outcome.Status);
        var text = ReadLogs(logs);
        Assert.Contains("sync failed origin=manual category=Error", text, StringComparison.Ordinal);
        Assert.DoesNotContain("chatgpt.com/api/auth/session", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToastProducingFailures_AlwaysHaveALogCategory()
    {
        foreach (var status in Enum.GetValues<AppSyncStatus>())
        {
            if (SyncFailurePresentation.ProducesSyncErrorToast(status))
            {
                Assert.True(SyncFailurePresentation.IsLoggedFailure(status));
                Assert.Contains($"category={status}", SyncFailurePresentation.LogLine(SyncOrigin.Auto, status), StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(AppSyncStatus.AuthenticationRequired)]
    [InlineData(AppSyncStatus.SignedOut)]
    [InlineData(AppSyncStatus.Offline)]
    [InlineData(AppSyncStatus.Forbidden)]
    [InlineData(AppSyncStatus.ChatGptTabRequired)]
    [InlineData(AppSyncStatus.PageBridgeUnavailable)]
    [InlineData(AppSyncStatus.ProviderSchemaMismatch)]
    [InlineData(AppSyncStatus.RateLimited)]
    [InlineData(AppSyncStatus.Error)]
    public void EveryToastProducingFailure_HasMatchingLogCategory(AppSyncStatus status)
    {
        Assert.True(SyncFailurePresentation.IsLoggedFailure(status));
        var line = SyncFailurePresentation.LogLine(SyncOrigin.Manual, status);
        Assert.Contains($"category={status}", line, StringComparison.Ordinal);
        Assert.Contains("origin=manual", line, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", line, StringComparison.OrdinalIgnoreCase);
        if (SyncFailurePresentation.ProducesSyncErrorToast(status))
        {
            Assert.Contains($"category={status}", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void IdenticalAutomaticFailure_ProducesOneToast()
    {
        var now = DateTimeOffset.Parse("2026-09-05T02:00:00Z");
        var first = default(SyncErrorToastState);
        Assert.True(SyncErrorNotificationGate.ShouldNotify(
            first, AppSyncStatus.Offline, UiText.ChatGptUnreachable, SyncOrigin.Auto, now, out var afterFirst));
        Assert.False(SyncErrorNotificationGate.ShouldNotify(
            afterFirst, AppSyncStatus.Offline, UiText.ChatGptUnreachable, SyncOrigin.Auto, now.AddMinutes(5), out var afterSecond));
        Assert.Equal(afterFirst.LastNotifiedAt, afterSecond.LastNotifiedAt);
        Assert.False(SyncErrorNotificationGate.ShouldNotify(
            afterSecond, AppSyncStatus.Offline, UiText.ChatGptUnreachable, SyncOrigin.Startup, now.AddMinutes(20), out _));
    }

    [Fact]
    public void DifferentFailureCategory_NotifiesImmediately()
    {
        var now = DateTimeOffset.Parse("2026-09-05T02:00:00Z");
        Assert.True(SyncErrorNotificationGate.ShouldNotify(
            default, AppSyncStatus.Offline, "offline", SyncOrigin.Auto, now, out var offline));
        Assert.True(SyncErrorNotificationGate.ShouldNotify(
            offline, AppSyncStatus.Forbidden, CompanionDiagnostics.Forbidden403, SyncOrigin.Auto, now.AddSeconds(10), out var forbidden));
        Assert.Equal(nameof(AppSyncStatus.Forbidden), forbidden.Category);
    }

    [Fact]
    public void SuccessfulSync_ResetsNotificationSuppression()
    {
        var settings = AppSettings.CreateDefaults();
        var now = DateTimeOffset.Parse("2026-09-05T03:00:00Z");
        SyncErrorNotificationGate.ShouldNotify(
            default, AppSyncStatus.Offline, "offline", SyncOrigin.Auto, now, out var notified);
        notified.WriteTo(settings);
        SyncErrorToastState.RememberFailureAttempt(settings, AppSyncStatus.Offline, now);
        Assert.False(string.IsNullOrWhiteSpace(settings.LastSyncErrorToastAt));
        SyncErrorNotificationGate.Cleared().WriteTo(settings);
        SyncErrorToastState.ClearFailureAttempt(settings);
        Assert.True(string.IsNullOrWhiteSpace(settings.LastSyncErrorToastCategory));
        Assert.True(string.IsNullOrWhiteSpace(settings.LastSyncFailureAt));
        Assert.True(SyncFailurePresentation.IsSuccessfulCompletion(AppSyncStatus.UpToDate));
        Assert.True(SyncErrorNotificationGate.ShouldNotify(
            SyncErrorToastState.FromSettings(settings),
            AppSyncStatus.Offline,
            "offline",
            SyncOrigin.Auto,
            now.AddMinutes(1),
            out _));
    }

    [Fact]
    public void PersistedSuppression_SurvivesReload()
    {
        var settings = AppSettings.CreateDefaults();
        var now = DateTimeOffset.Parse("2026-09-05T04:00:00Z");
        SyncErrorNotificationGate.ShouldNotify(
            default, AppSyncStatus.SignedOut, UiText.ChatGptSignedOut, SyncOrigin.Auto, now, out var notified);
        notified.WriteTo(settings);
        var reloaded = SyncErrorToastState.FromSettings(settings);
        Assert.False(SyncErrorNotificationGate.ShouldNotify(
            reloaded, AppSyncStatus.SignedOut, UiText.ChatGptSignedOut, SyncOrigin.Auto, now.AddMinutes(10), out _));
    }

    [Fact]
    public void ManualRefresh_MayNotifyAfterShortMinimum()
    {
        var now = DateTimeOffset.Parse("2026-09-05T05:00:00Z");
        Assert.True(SyncErrorNotificationGate.ShouldNotify(
            default, AppSyncStatus.Offline, "offline", SyncOrigin.Auto, now, out var notified));
        Assert.False(SyncErrorNotificationGate.ShouldNotify(
            notified, AppSyncStatus.Offline, "offline", SyncOrigin.Manual, now.AddMinutes(1), out _));
        Assert.True(SyncErrorNotificationGate.ShouldNotify(
            notified, AppSyncStatus.Offline, "offline", SyncOrigin.Manual, now.AddMinutes(2), out _));
    }

    [Fact]
    public void RepeatedFlyoutOpensWithinCooldown_DoNotRetriggerFailedAutoSync()
    {
        var now = DateTimeOffset.Parse("2026-09-05T06:00:00Z");
        var failedAt = now.AddSeconds(-15);
        Assert.False(FlyoutAutoSyncPolicy.ShouldStartStaleAutoSync(
            true, false, now, lastSuccessfulSync: null, 15, failedAt));
        Assert.False(FlyoutAutoSyncPolicy.ShouldStartStaleAutoSync(
            true, false, now.AddMinutes(1), lastSuccessfulSync: null, 15, failedAt));
        Assert.True(FlyoutAutoSyncPolicy.ShouldStartStaleAutoSync(
            true, false, now.AddMinutes(2), lastSuccessfulSync: null, 15, failedAt));
        Assert.False(FlyoutAutoSyncPolicy.ShouldStartStaleAutoSync(
            true, fromTaskbarStrip: true, now.AddMinutes(3), null, 15, null));
    }

    [Fact]
    public void ManualRefresh_RemainsAvailableDuringFlyoutCooldown()
    {
        Assert.True(FlyoutAutoSyncPolicy.AllowsManualRefresh);
        var now = DateTimeOffset.Parse("2026-09-05T07:00:00Z");
        Assert.False(FlyoutAutoSyncPolicy.ShouldStartStaleAutoSync(
            true, false, now, null, 15, now.AddSeconds(-5)));
        Assert.True(FlyoutAutoSyncPolicy.AllowsManualRefresh);
        var coordinator = new CombinedRefreshCoordinator(
            (_, _) => Task.FromResult(new SyncOutcome(AppSyncStatus.Offline, "offline", 0)),
            _ => Task.FromResult(new CodexRefreshResult(
                CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable),
                false,
                null)));
        Assert.True(coordinator.RefreshButtonEnabled);
    }

    [Fact]
    public void AppWiresOriginCooldownAndToastGate()
    {
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("FlyoutAutoSyncPolicy.ShouldStartStaleAutoSync", app, StringComparison.Ordinal);
        Assert.Contains("SyncOrigin.FlyoutStaleRefresh", app, StringComparison.Ordinal);
        Assert.Contains("SyncOrigin.Manual", app, StringComparison.Ordinal);
        Assert.Contains("TrySyncError", app, StringComparison.Ordinal);
        Assert.Contains("ResetSyncErrorSuppression", app, StringComparison.Ordinal);
        var engine = File.ReadAllText(Find("src/ProMeter.Core/Services/SyncEngine.cs"));
        Assert.Contains("LogSyncFailure(options.Origin, LastStatus)", engine, StringComparison.Ordinal);
        Assert.Contains("ex.IsOffline", engine, StringComparison.Ordinal);
    }

    private static (SyncEngine Engine, IChatGptProvider Provider, AppSettings Settings) CreateOfflineHarness(
        string dir,
        string logs,
        Func<string, ConversationLoadResult> load)
    {
        var store = new SqliteStore(Path.Combine(dir, "offline.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(logs));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(
            new ConversationIndexItem { Id = "conv-a", UpdateTime = now, CreateTime = now - 10 },
            ConversationFixtures.NormalPro("conv-a", now));
        fixture.LoadOverride = load;
        return (engine, fixture, AppSettings.CreateDefaults());
    }

    private static string ReadLogs(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return "";
        }

        return string.Join(Environment.NewLine, Directory.GetFiles(directory, "*.log").Select(File.ReadAllText));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class ThrowingAccountProvider : IChatGptProvider
    {
        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("https://chatgpt.com/api/auth/session token=secret-token");

        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelCatalogEntry>>([]);

        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());

        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());

        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectListResult());

        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationLoadResult());

        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuotaMetadataSet());
    }

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
