using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class IncrementalSyncTests
{
    [Fact]
    public async Task UnchangedConversation_IsNotFetchedAgain()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "test.db"));
        var models = new ModelNormalizer();
        var parser = new ConversationParser(models);
        var log = new AppLog(Path.Combine(dir, "logs"));
        var engine = new SyncEngine(store, parser, models, log);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem
        {
            Id = "conv-normal",
            UpdateTime = now,
            CreateTime = now - 60
        };
        fixture.AddConversation(item, ConversationFixtures.NormalPro("conv-normal", now));
        var provider = new CountingProvider(fixture);

        var settings = AppSettings.CreateDefaults();
        settings.ResetWeekday = DayOfWeek.Monday;
        settings.ResetTimeZoneId = "UTC";

        await engine.SyncAsync(provider, settings, force: true);
        var first = provider.BodyFetches;
        Assert.True(first >= 1);

        await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(first, provider.BodyFetches);
    }

    [Fact]
    public async Task UpdatedConversation_IsFetchedAgain()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "test.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-normal", UpdateTime = now, CreateTime = now - 60 };
        fixture.AddConversation(item, ConversationFixtures.NormalPro("conv-normal", now));
        var provider = new CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        await engine.SyncAsync(provider, settings, true);
        item.UpdateTime = now + 120;
        await engine.SyncAsync(provider, settings, true);
        Assert.Equal(2, provider.BodyFetches);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class CountingProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;
        public int BodyFetches { get; private set; }

        public CountingProvider(FixtureChatGptProvider inner) => _inner = inner;

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) => _inner.GetAccountStatusAsync(cancellationToken);
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) => _inner.GetModelCatalogAsync(cancellationToken);
        public Task<IReadOnlyList<ConversationIndexItem>> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetConversationIndexAsync(archived, minUpdateTime, cancellationToken);
        public Task<IReadOnlyList<ConversationIndexItem>> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);
        public Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken = default) => _inner.GetProjectsAsync(cancellationToken);
        public Task<IReadOnlyList<ConversationIndexItem>> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken);
        public Task<JsonNode?> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            BodyFetches++;
            return _inner.GetConversationMessagesAsync(conversationId, cancellationToken);
        }
        public Task<QuotaMetadata> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) => _inner.TryGetQuotaMetadataAsync(cancellationToken);
    }
}
