using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class DuplicateConversationSyncTests
{
    [Fact]
    public async Task NormalAndProject_FetchesBodyOnceAndMergesProjectId()
    {
        var (engine, store, provider, settings, now) = CreateEngine();
        var body = ConversationFixtures.NormalPro("dup-np", now);
        provider.Inner.AddConversation(
            new ConversationIndexItem { Id = "dup-np", UpdateTime = now, CreateTime = now - 60 },
            body);
        provider.Inner.AddProject(
            new ProjectInfo { Id = "proj-1" },
            [(new ConversationIndexItem { Id = "dup-np", UpdateTime = now, CreateTime = now - 60 }, body)]);

        var outcome = await engine.SyncAsync(provider, settings, force: true);

        Assert.Equal(AppSyncStatus.UpToDate, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
        Assert.Equal("proj-1", store.GetConversation("dup-np")?.ProjectId);
        Assert.Contains(store.GetUsageEvents(), e => e.ConversationId == "dup-np" && e.ProjectId == "proj-1");
    }

    [Fact]
    public async Task NormalAndArchived_FetchesBodyOnceAndMarksArchived()
    {
        var (engine, store, provider, settings, now) = CreateEngine();
        var body = ConversationFixtures.NormalPro("dup-na", now);
        provider.Inner.AddConversation(
            new ConversationIndexItem { Id = "dup-na", UpdateTime = now, CreateTime = now - 60, Archived = false },
            body);
        provider.Inner.AddConversation(
            new ConversationIndexItem { Id = "dup-na", UpdateTime = now, CreateTime = now - 60, Archived = true },
            body);

        await engine.SyncAsync(provider, settings, force: true);

        Assert.Equal(1, provider.BodyFetches);
        Assert.True(store.GetConversation("dup-na")?.Archived);
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
    }

    [Fact]
    public async Task TwoProjectIndexes_FetchBodyOnce()
    {
        var (engine, store, provider, settings, now) = CreateEngine();
        var body = ConversationFixtures.NormalPro("dup-pp", now);
        provider.Inner.AddProject(
            new ProjectInfo { Id = "proj-a" },
            [(new ConversationIndexItem { Id = "dup-pp", UpdateTime = now, CreateTime = now - 60 }, body)]);
        provider.Inner.AddProject(
            new ProjectInfo { Id = "proj-b" },
            [(new ConversationIndexItem { Id = "dup-pp", UpdateTime = now, CreateTime = now - 60 }, body)]);

        await engine.SyncAsync(provider, settings, force: true);

        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal("proj-a", store.GetConversation("dup-pp")?.ProjectId);
    }

    [Fact]
    public async Task DuplicateFailure_CountsOnceAndLogsCategory()
    {
        var (engine, store, provider, settings, now) = CreateEngine();
        provider.Inner.LoadOverride = _ => new ConversationLoadResult
        {
            Complete = false,
            Conversation = null,
            Diagnostics = ["PayloadTooLarge"]
        };
        provider.Inner.AddConversation(
            new ConversationIndexItem { Id = "fail-dup", UpdateTime = now, CreateTime = now - 60 },
            ConversationFixtures.NormalPro("fail-dup", now));
        provider.Inner.AddProject(
            new ProjectInfo { Id = "proj-fail" },
            [(new ConversationIndexItem { Id = "fail-dup", UpdateTime = now, CreateTime = now - 60 }, ConversationFixtures.NormalPro("fail-dup", now))]);

        await engine.SyncAsync(provider, settings, force: true);

        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
        Assert.Equal(2, engine.LastCoverage.ScanAttempts);
        Assert.Equal(1, engine.LastCoverage.UniqueConversations);
        var logs = Directory.GetFiles(provider.LogDirectory, "*.log").SelectMany(File.ReadAllLines).ToList();
        Assert.Contains(logs, line => line.Contains("conversation fetch failed id=fail-dup category=PayloadTooLarge status=0", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("fetch failed conversation=", StringComparison.Ordinal));
        Assert.Equal("proj-fail", store.GetConversation("fail-dup")?.ProjectId);
    }

    [Fact]
    public void Classifier_MapsHttpStatusAndTimeout()
    {
        Assert.Equal(("HttpStatus", 403), SyncFailureClassifier.Classify(new ChatGptProviderException("rejected", 403)));
        Assert.Equal(("PayloadTooLarge", 0), SyncFailureClassifier.Classify(new ChatGptProviderException("payload too large", 0)));
        Assert.Equal(("BridgeTimeout", 0), SyncFailureClassifier.Classify(new ChatGptProviderException("native bridge timed out", 0)));
        Assert.Equal("IncompletePagination", SyncFailureClassifier.ClassifyLoad(new ConversationLoadResult
        {
            Complete = false,
            Diagnostics = ["pagination stopped early"]
        }));
    }

    [Fact]
    public async Task ForceTrue_RefetchesAcrossRunsButNotInsideOneRun()
    {
        var (engine, _, provider, settings, now) = CreateEngine();
        var body = ConversationFixtures.NormalPro("force-dup", now);
        provider.Inner.AddConversation(
            new ConversationIndexItem { Id = "force-dup", UpdateTime = now, CreateTime = now - 60 },
            body);
        provider.Inner.AddProject(
            new ProjectInfo { Id = "proj-force" },
            [(new ConversationIndexItem { Id = "force-dup", UpdateTime = now, CreateTime = now - 60 }, body)]);

        await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(1, provider.BodyFetches);
        await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(2, provider.BodyFetches);
    }

    private static (SyncEngine Engine, SqliteStore Store, CountingInner Provider, AppSettings Settings, double Now) CreateEngine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var logDir = Path.Combine(dir, "logs");
        var store = new SqliteStore(Path.Combine(dir, "test.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(logDir));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        return (engine, store, new CountingInner(fixture, logDir), settings, now);
    }

    internal sealed class CountingInner : IChatGptProvider
    {
        public FixtureChatGptProvider Inner { get; }
        public string LogDirectory { get; }
        public int BodyFetches { get; private set; }

        public CountingInner(FixtureChatGptProvider inner, string logDirectory)
        {
            Inner = inner;
            LogDirectory = logDirectory;
        }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) => Inner.GetAccountStatusAsync(cancellationToken);
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) => Inner.GetModelCatalogAsync(cancellationToken);
        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Inner.GetConversationIndexAsync(archived, minUpdateTime, cancellationToken);
        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);
        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) => Inner.GetProjectsAsync(cancellationToken);
        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            Inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken);
        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            BodyFetches++;
            return Inner.GetConversationMessagesAsync(conversationId, cancellationToken);
        }
        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) => Inner.TryGetQuotaMetadataAsync(cancellationToken);
    }
}
