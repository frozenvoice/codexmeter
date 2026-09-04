using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

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
        var completedLabel = DisplayFormatting.LastSyncLabel(engine.LastSyncCompleted);
        var startLabel = DisplayFormatting.LastSyncLabel(start);
        Assert.Equal(engine.LastSyncCompleted!.Value.ToLocalTime().ToString("HH:mm"), completedLabel);
        Assert.NotEqual(startLabel, completedLabel);
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
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
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
}
