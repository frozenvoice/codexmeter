using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class IncrementalSyncTests
{
    [Fact]
    public async Task UnchangedConversation_IsNotFetchedAgain()
    {
        var (engine, store, provider, settings, item) = CreateHarness();
        await engine.SyncAsync(provider, settings, force: true);
        var first = provider.BodyFetches;
        Assert.True(first >= 1);

        await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(first, provider.BodyFetches);
        Assert.Equal(item.UpdateTime, store.GetConversation(item.Id)?.LastSeenUpdateTime);
    }

    [Fact]
    public async Task ForceTrue_RefetchesAllPeriodConversations()
    {
        var (engine, _, provider, settings, _) = CreateHarness();
        await engine.SyncAsync(provider, settings, force: true);
        var first = provider.BodyFetches;
        await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(first * 2, provider.BodyFetches);
    }

    [Fact]
    public async Task UpdatedConversation_IsFetchedAgain()
    {
        var (engine, _, provider, settings, item) = CreateHarness();
        await engine.SyncAsync(provider, settings, force: true);
        item.UpdateTime += 120;
        await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(2, provider.BodyFetches);
    }

    [Fact]
    public async Task IsoTimestamps_DriveIncrementalSync()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "iso.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var firstIso = "2026-09-01T12:00:00Z";
        var secondIso = "2026-09-03T15:30:00.250Z";
        var firstUnix = TimestampParser.ToUnixSeconds(TimestampParser.Parse(firstIso));
        var secondUnix = TimestampParser.ToUnixSeconds(TimestampParser.Parse(secondIso));
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem
        {
            Id = "conv-iso",
            UpdateTime = firstUnix,
            CreateTime = firstUnix - 60
        };
        fixture.AddConversation(item, ConversationFixtures.NormalPro("conv-iso", firstUnix));
        var provider = new CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";

        await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(1, provider.BodyFetches);

        await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(1, provider.BodyFetches);

        item.UpdateTime = secondUnix;
        await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(secondUnix, store.GetConversation(item.Id)?.LastSeenUpdateTime);
    }

    private static (SyncEngine Engine, SqliteStore Store, CountingProvider Provider, AppSettings Settings, ConversationIndexItem Item) CreateHarness()
    {
        var dir = NewTempDir();
        var store = new SqliteStore(Path.Combine(dir, "test.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-normal", UpdateTime = now, CreateTime = now - 60 };
        fixture.AddConversation(item, ConversationFixtures.NormalPro("conv-normal", now));
        var settings = AppSettings.CreateDefaults();
        settings.ResetWeekday = DayOfWeek.Monday;
        settings.ResetTimeZoneId = "UTC";
        return (engine, store, new CountingProvider(fixture), settings, item);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal sealed class CountingProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;
        public int BodyFetches { get; private set; }

        public CountingProvider(FixtureChatGptProvider inner) => _inner = inner;

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) => _inner.GetAccountStatusAsync(cancellationToken);
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) => _inner.GetModelCatalogAsync(cancellationToken);
        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetConversationIndexAsync(archived, minUpdateTime, cancellationToken);
        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);
        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) => _inner.GetProjectsAsync(cancellationToken);
        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken);
        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            BodyFetches++;
            return _inner.GetConversationMessagesAsync(conversationId, cancellationToken);
        }
        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) => _inner.TryGetQuotaMetadataAsync(cancellationToken);
    }
}
