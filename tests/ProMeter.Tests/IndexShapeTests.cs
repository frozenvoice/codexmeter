using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class IndexShapeTests
{
    [Fact]
    public void UnknownConversationIndexShape_IsSchemaMismatchNotEmptySuccess()
    {
        var parsed = AccountParser.ParseConversationIndex(new JsonObject { ["oops"] = true, ["data"] = new JsonObject() }, false);
        Assert.False(parsed.RecognizedShape);
        Assert.Empty(parsed.Items);
    }

    [Fact]
    public void UnknownProjectsShape_IsSchemaMismatchNotEmptySuccess()
    {
        var parsed = AccountParser.ParseProjects(new JsonObject { ["sidebar"] = true });
        Assert.False(parsed.RecognizedShape);
        Assert.Empty(parsed.Projects);
        Assert.True(AccountParser.ParseProjects(new JsonObject { ["items"] = new JsonArray() }).RecognizedShape);
    }

    [Fact]
    public void MissingUpdateTime_IsTimestampIncomplete()
    {
        var parsed = AccountParser.ParseConversationIndex(new JsonObject
        {
            ["items"] = new JsonArray
            {
                new JsonObject { ["id"] = "no-time", ["title"] = "x" }
            }
        }, false);
        Assert.True(parsed.RecognizedShape);
        Assert.False(parsed.TimestampComplete);
        Assert.Equal(1, parsed.MissingTimestamps);
        Assert.Equal(0, parsed.Items[0].UpdateTime);
    }

    [Fact]
    public async Task UnknownIndexShape_PropagatesProviderSchemaMismatch()
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 200,
            Body = """{"unexpected":{"shape":true}}"""
        });
        var result = await new ChatGptProvider(transport).GetConversationIndexAsync(false);
        Assert.True(result.SchemaMismatch);
        Assert.Empty(result.Items);
        Assert.False(result.ReachedCutoff);

        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "shape.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var fixture = new FixtureChatGptProvider();
        var provider = new MismatchIndexProvider();
        var outcome = await engine.SyncAsync(provider, AppSettings.CreateDefaults(), true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.NormalChats);
        Assert.NotEqual(CoverageConfidence.HighConfidence, engine.LastCoverage.Confidence);
        Assert.NotEqual(CoverageConfidence.Authoritative, engine.LastCoverage.Confidence);
    }

    [Fact]
    public async Task MissingUpdateTime_IsRescannedEverySync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "ts.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-notime", UpdateTime = 0, CreateTime = 0 };
        fixture.AddConversation(item, ConversationFixtures.NormalPro("conv-notime"));
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        await engine.SyncAsync(provider, settings, true);
        await engine.SyncAsync(provider, settings, false);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(ConversationScanStatus.Incomplete, store.GetConversation(item.Id)?.Status);
        Assert.Equal(0, store.GetConversation(item.Id)?.LastSeenUpdateTime);
        Assert.Null(store.GetConversation(item.Id)?.LastSuccessfulScan);
    }

    private sealed class MismatchIndexProvider : IChatGptProvider
    {
        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountStatus { IsSignedIn = true, Email = "x@example.com" });
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelCatalogEntry>>([]);
        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult { SchemaMismatch = true, Incomplete = true });
        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());
        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectListResult());
        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ConversationIndexResult());
        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(ConversationDetailLoader.FromFixture(null));
        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuotaMetadataSet());
    }
}
