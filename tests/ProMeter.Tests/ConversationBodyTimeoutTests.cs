using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ConversationBodyTimeoutTests
{
    [Fact]
    public async Task OneConversationBridgeTimeout_ContinuesAsPartialData()
    {
        var (engine, provider, settings) = CreateHarness((id, _) =>
        {
            if (id == "conv-a")
            {
                throw new ChatGptProviderException(CompanionBridgeProtocol.TimeoutError);
            }

            return Task.FromResult(ConversationDetailLoader.FromFixture(ConversationFixtures.NormalPro(id)));
        });
        var outcome = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
    }

    [Fact]
    public async Task LocalBodyTimeout_ContinuesAndFetchesNextConversation()
    {
        var (engine, provider, settings) = CreateHarness(async (id, token) =>
        {
            if (id == "conv-a")
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return ConversationDetailLoader.FromFixture(ConversationFixtures.NormalPro(id));
        });
        engine.ConversationBodyTimeout = TimeSpan.FromMilliseconds(80);
        engine.ConsecutiveBodyTimeoutLimit = 3;
        var outcome = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
    }

    [Fact]
    public async Task ConsecutiveBodyTimeouts_EscalateToBridgeTimeout()
    {
        var (engine, provider, settings) = CreateThreeHarness();
        engine.ConversationBodyTimeout = TimeSpan.FromMilliseconds(80);
        engine.ConsecutiveBodyTimeoutLimit = 2;
        var outcome = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.BridgeTimeout, outcome.Status);
        Assert.Equal(2, provider.BodyFetches);
    }

    [Fact]
    public async Task IndexBridgeTimeout_AbortsSync()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "index-timeout.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var provider = new IndexTimeoutProvider();
        var outcome = await engine.SyncAsync(provider, AppSettings.CreateDefaults(), SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.BridgeTimeout, outcome.Status);
        Assert.Equal(0, provider.BodyFetches);
    }

    [Fact]
    public async Task CompanionDisconnectedDuringBody_AbortsSync()
    {
        var (engine, provider, settings) = CreateHarness((_, _) =>
            throw new ChatGptProviderException(CompanionBridgeProtocol.NotConnectedError));
        var outcome = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.CompanionDisconnected, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
    }

    [Fact]
    public async Task RateLimitExhaustingBodyBudget_AbortsSyncInsteadOfBodyTimeout()
    {
        var (engine, provider, settings) = CreateHarness((_, _) =>
            throw new ChatGptProviderException("Too Many Requests", 429));
        engine.ConversationBodyTimeout = TimeSpan.FromMilliseconds(50);
        engine.RetryBaseDelay = TimeSpan.FromMilliseconds(500);
        engine.RetryAttempts = 5;
        var outcome = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.RateLimited, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
    }

    [Fact]
    public async Task UserCancellation_StillCancels()
    {
        using var cts = new CancellationTokenSource();
        var (engine, provider, settings) = CreateHarness(async (_, token) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return ConversationDetailLoader.FromFixture(ConversationFixtures.NormalPro("x"));
        });
        var outcome = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental, cts.Token);
        Assert.Equal(AppSyncStatus.Error, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
    }

    private static (SyncEngine Engine, TimedBodyProvider Provider, AppSettings Settings) CreateHarness(
        Func<string, CancellationToken, Task<ConversationLoadResult>> load)
    {
        var dir = NewTempDir();
        var store = new SqliteStore(Path.Combine(dir, "body-timeout.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-a", UpdateTime = now, CreateTime = now - 10 }, ConversationFixtures.NormalPro("conv-a", now));
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-b", UpdateTime = now - 1, CreateTime = now - 20 }, ConversationFixtures.NormalPro("conv-b", now - 1));
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        return (engine, new TimedBodyProvider(fixture, load), settings);
    }

    private static (SyncEngine Engine, TimedBodyProvider Provider, AppSettings Settings) CreateThreeHarness()
    {
        var dir = NewTempDir();
        var store = new SqliteStore(Path.Combine(dir, "body-timeout3.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-a", UpdateTime = now }, ConversationFixtures.NormalPro("conv-a", now));
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-b", UpdateTime = now - 1 }, ConversationFixtures.NormalPro("conv-b", now - 1));
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-c", UpdateTime = now - 2 }, ConversationFixtures.NormalPro("conv-c", now - 2));
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        return (engine, new TimedBodyProvider(fixture, async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return ConversationDetailLoader.FromFixture(ConversationFixtures.NormalPro("x"));
        }), settings);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class TimedBodyProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;
        private readonly Func<string, CancellationToken, Task<ConversationLoadResult>> _load;
        public int BodyFetches { get; private set; }

        public TimedBodyProvider(FixtureChatGptProvider inner, Func<string, CancellationToken, Task<ConversationLoadResult>> load)
        {
            _inner = inner;
            _load = load;
        }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) => _inner.GetAccountStatusAsync(cancellationToken);
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) => _inner.GetModelCatalogAsync(cancellationToken);
        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetConversationIndexAsync(archived, minUpdateTime, cancellationToken);
        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);
        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) => _inner.GetProjectsAsync(cancellationToken);
        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken);
        public async Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            BodyFetches++;
            return await _load(conversationId, cancellationToken);
        }
        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) => _inner.TryGetQuotaMetadataAsync(cancellationToken);
    }

    private sealed class IndexTimeoutProvider : IChatGptProvider
    {
        public int BodyFetches { get; private set; }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountStatus { IsSignedIn = true, PlanType = "pro" });
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelCatalogEntry>>([]);
        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            throw new ChatGptProviderException(CompanionBridgeProtocol.TimeoutError);
        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());
        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectListResult());
        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());
        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            BodyFetches++;
            throw new InvalidOperationException("body should not be fetched after index timeout");
        }
        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuotaMetadataSet());
    }
}
