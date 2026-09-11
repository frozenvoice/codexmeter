using System.Globalization;
using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

public class LastSyncCompletionTests
{
    [Fact]
    public async Task LastSyncCompleted_UsesCompletionTimeNotPeriodReference()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "sync.db"));
        var models = new ModelNormalizer();
        var start = new DateTimeOffset(2026, 9, 4, 16, 12, 0, TimeSpan.Zero);
        var clock = new MutableClock(start);
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var now = start.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(
            new ConversationIndexItem { Id = "conv-slow", UpdateTime = now, CreateTime = now - 60 },
            ConversationFixtures.NormalPro("conv-slow", now));
        var provider = new ClockAdvancingProvider(fixture, clock, TimeSpan.FromMinutes(3));
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";

        var outcome = await engine.SyncAsync(provider, settings, force: true);

        Assert.Equal(AppSyncStatus.UpToDate, outcome.Status);
        Assert.Equal(start.AddMinutes(3), engine.LastSyncCompleted);
        Assert.NotEqual(start, engine.LastSyncCompleted);
        Assert.NotNull(engine.LastSyncCompleted);
        Assert.Equal(
            engine.LastSyncCompleted!.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            store.GetState(SyncEngine.LastSyncCompletedStateKey));
        var completedLabel = DisplayFormatting.LastSyncLabel(engine.LastSyncCompleted);
        var startLabel = DisplayFormatting.LastSyncLabel(start);
        Assert.Equal(engine.LastSyncCompleted!.Value.ToLocalTime().ToString("HH:mm"), completedLabel);
        Assert.NotEqual(startLabel, completedLabel);
    }

    [Fact]
    public async Task NewSyncEngine_RestoresPersistedLastSyncCompleted()
    {
        var dir = NewTempDir();
        var databasePath = Path.Combine(dir, "restart.db");
        var completed = new DateTimeOffset(2026, 9, 4, 18, 45, 37, TimeSpan.Zero);
        using (var firstStore = new SqliteStore(databasePath))
        {
            var firstModels = new ModelNormalizer();
            var firstEngine = new SyncEngine(
                firstStore,
                new ConversationParser(firstModels),
                firstModels,
                new AppLog(Path.Combine(dir, "first-logs")),
                new MutableClock(completed));

            var outcome = await firstEngine.SyncAsync(
                new FixtureChatGptProvider(),
                AppSettings.CreateDefaults(),
                force: true);

            Assert.Equal(AppSyncStatus.UpToDate, outcome.Status);
            Assert.Equal(completed, firstEngine.LastSyncCompleted);
        }

        using var reopenedStore = new SqliteStore(databasePath);
        var reopenedModels = new ModelNormalizer();
        var reopenedEngine = new SyncEngine(
            reopenedStore,
            new ConversationParser(reopenedModels),
            reopenedModels,
            new AppLog(Path.Combine(dir, "reopened-logs")));

        Assert.Equal(completed, reopenedEngine.LastSyncCompleted);
        Assert.Equal(
            completed.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
            DisplayFormatting.LastSyncLabel(reopenedEngine.LastSyncCompleted));
    }

    [Fact]
    public async Task PartialCompletion_PersistsLastSyncCompleted()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "partial.db"));
        var models = new ModelNormalizer();
        var completed = new DateTimeOffset(2026, 9, 5, 4, 1, 2, TimeSpan.FromHours(9));
        var engine = new SyncEngine(
            store,
            new ConversationParser(models),
            models,
            new AppLog(Path.Combine(dir, "logs")),
            new MutableClock(completed));
        var fixture = new FixtureChatGptProvider { IndexIncomplete = true };

        var outcome = await engine.SyncAsync(fixture, AppSettings.CreateDefaults(), force: true);

        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.Equal(completed, engine.LastSyncCompleted);
        Assert.Equal(TimeSpan.Zero, engine.LastSyncCompleted!.Value.Offset);
        Assert.Equal(
            completed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            store.GetState(SyncEngine.LastSyncCompletedStateKey));
    }

    [Fact]
    public async Task FailedSync_DoesNotReplacePreviousPersistedCompletion()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "failed.db"));
        var previous = new DateTimeOffset(2026, 9, 4, 17, 45, 37, TimeSpan.Zero);
        var persisted = previous.ToString("O", CultureInfo.InvariantCulture);
        store.SetState(SyncEngine.LastSyncCompletedStateKey, persisted);
        var models = new ModelNormalizer();
        var engine = new SyncEngine(
            store,
            new ConversationParser(models),
            models,
            new AppLog(Path.Combine(dir, "logs")));

        var outcome = await engine.SyncAsync(
            new UnauthorizedProvider(),
            AppSettings.CreateDefaults(),
            force: true);

        Assert.Equal(AppSyncStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(previous, engine.LastSyncCompleted);
        Assert.Equal(persisted, store.GetState(SyncEngine.LastSyncCompletedStateKey));
    }

    [Fact]
    public async Task CancellationAtCompletion_DoesNotReplacePreviousPersistedCompletion()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "cancelled.db"));
        var previous = new DateTimeOffset(2026, 9, 4, 17, 45, 37, TimeSpan.Zero);
        var persisted = previous.ToString("O", CultureInfo.InvariantCulture);
        store.SetState(SyncEngine.LastSyncCompletedStateKey, persisted);
        var models = new ModelNormalizer();
        var engine = new SyncEngine(
            store,
            new ConversationParser(models),
            models,
            new AppLog(Path.Combine(dir, "logs")),
            new MutableClock(previous.AddHours(1)));
        using var cancellation = new CancellationTokenSource();
        engine.ProgressChanged += progress =>
        {
            if (progress.Phase == DisplayFormatting.StatusLabel(AppSyncStatus.UpToDate))
            {
                cancellation.Cancel();
            }
        };

        var outcome = await engine.SyncAsync(
            new FixtureChatGptProvider(),
            AppSettings.CreateDefaults(),
            force: true,
            cancellation.Token);

        Assert.Equal(AppSyncStatus.Error, outcome.Status);
        Assert.Equal(previous, engine.LastSyncCompleted);
        Assert.Equal(persisted, store.GetState(SyncEngine.LastSyncCompletedStateKey));
    }

    [Fact]
    public void MalformedPersistedCompletion_IsIgnoredWithoutChangingIt()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "malformed.db"));
        const string malformed = "not-an-iso-timestamp";
        store.SetState(SyncEngine.LastSyncCompletedStateKey, malformed);
        var models = new ModelNormalizer();
        SyncEngine? engine = null;

        var exception = Record.Exception(() => engine = new SyncEngine(
            store,
            new ConversationParser(models),
            models,
            new AppLog(Path.Combine(dir, "logs"))));

        Assert.Null(exception);
        Assert.NotNull(engine);
        Assert.Null(engine!.LastSyncCompleted);
        Assert.Equal(malformed, store.GetState(SyncEngine.LastSyncCompletedStateKey));
    }

    [Fact]
    public async Task AuthFailure_DoesNotSetLastSyncCompleted()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "auth.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var fixture = new FixtureChatGptProvider(new AccountStatus { IsSignedIn = false });
        var settings = AppSettings.CreateDefaults();

        var outcome = await engine.SyncAsync(fixture, settings, force: true);

        Assert.Equal(AppSyncStatus.SignedOut, outcome.Status);
        Assert.Null(engine.LastSyncCompleted);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class ClockAdvancingProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;
        private readonly MutableClock _clock;
        private readonly TimeSpan _advance;

        public ClockAdvancingProvider(FixtureChatGptProvider inner, MutableClock clock, TimeSpan advance)
        {
            _inner = inner;
            _clock = clock;
            _advance = advance;
        }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            _inner.GetAccountStatusAsync(cancellationToken);
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            _inner.GetModelCatalogAsync(cancellationToken);
        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetConversationIndexAsync(archived, minUpdateTime, cancellationToken);
        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);
        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            _inner.GetProjectsAsync(cancellationToken);
        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken);
        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            _inner.TryGetQuotaMetadataAsync(cancellationToken);

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            _clock.UtcNow = _clock.UtcNow.Add(_advance);
            return _inner.GetConversationMessagesAsync(conversationId, cancellationToken);
        }
    }

    private sealed class UnauthorizedProvider : IChatGptProvider
    {
        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            throw new ChatGptProviderException("Authentication required.", 401);

        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account failure must abort before models are requested.");

        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account failure must abort before indexes are requested.");

        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account failure must abort before archived indexes are requested.");

        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account failure must abort before projects are requested.");

        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account failure must abort before project conversations are requested.");

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account failure must abort before bodies are requested.");

        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account failure must abort before quota metadata is requested.");
    }
}
