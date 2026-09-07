using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class FatalProviderSyncTests
{
    [Fact]
    public async Task UnauthorizedDuringBodyFetch_AbortsImmediately()
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(id =>
            throw new ChatGptProviderException("Authentication required.", 401));
        var outcome = await engine.SyncAsync(provider, settings, true);
        Assert.Equal(AppSyncStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
    }

    [Fact]
    public async Task ExhaustedRateLimit_DoesNotFetchLaterConversations()
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(id =>
            throw new ChatGptProviderException("Rate limited.", 429, "0"));
        engine.RetryAttempts = 3;
        engine.RetryBaseDelay = TimeSpan.Zero;
        var outcome = await engine.SyncAsync(provider, settings, true);
        Assert.Equal(AppSyncStatus.RateLimited, outcome.Status);
        Assert.Equal(3, provider.BodyFetches);
    }

    [Fact]
    public async Task OfflineDuringBodyFetch_ReturnsOffline()
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(_ =>
            throw new ChatGptProviderException("offline", 0));
        var outcome = await engine.SyncAsync(provider, settings, true);
        Assert.Equal(AppSyncStatus.Offline, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
    }

    [Fact]
    public async Task SchemaMismatch_RemainsConversationScoped()
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(id =>
        {
            if (id == "conv-a")
            {
                return new ConversationLoadResult
                {
                    Complete = false,
                    SchemaMismatch = true,
                    Diagnostics = ["mapping incomplete"]
                };
            }

            return ConversationDetailLoader.FromFixture(ConversationFixtures.NormalPro(id));
        });
        var outcome = await engine.SyncAsync(provider, settings, true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.Equal(2, provider.BodyFetches);
        Assert.True(engine.LastCoverage.ConversationIncomplete);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
    }

    [Fact]
    public async Task ArchivedIndexUnauthorized_AbortsSync()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "arch.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var fixture = new FixtureChatGptProvider();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-a", UpdateTime = now }, ConversationFixtures.NormalPro("conv-a", now));
        var provider = new ThrowingIndexProvider(fixture) { ThrowOnArchived = true };
        var outcome = await engine.SyncAsync(provider, AppSettings.CreateDefaults(), true);
        Assert.Equal(AppSyncStatus.AuthenticationRequired, outcome.Status);
    }

    [Fact]
    public async Task AccountsCheckRateLimit_DoesNotCallMe_AndAbortsSync()
    {
        var paths = new List<string>();
        var transport = new PaginationTests.ScriptedTransport((_, path) =>
        {
            paths.Add(path);
            if (path == ChatGptEndpoints.Session)
            {
                return SessionOk();
            }

            if (path == ChatGptEndpoints.AccountsCheck)
            {
                return new ProviderResponse { Status = 429, RetryAfter = "0", Error = "rate limited" };
            }

            return new ProviderResponse { Status = 500, Error = "unexpected " + path };
        });
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "rl.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        engine.RetryAttempts = 1;
        engine.RetryBaseDelay = TimeSpan.Zero;
        var outcome = await engine.SyncAsync(new ChatGptProvider(transport), AppSettings.CreateDefaults(), true);
        Assert.Equal(AppSyncStatus.RateLimited, outcome.Status);
        Assert.DoesNotContain(paths, path => path.Equals(ChatGptEndpoints.Me, StringComparison.Ordinal));
        Assert.Contains(ChatGptEndpoints.AccountsCheck, paths);
    }

    [Fact]
    public async Task AccountsCheckUnauthorized_AbortsWithoutMe()
    {
        var paths = new List<string>();
        var transport = new PaginationTests.ScriptedTransport((_, path) =>
        {
            paths.Add(path);
            if (path == ChatGptEndpoints.Session)
            {
                return SessionOk();
            }

            if (path == ChatGptEndpoints.AccountsCheck)
            {
                return new ProviderResponse { Status = 401, Error = "unauthorized" };
            }

            return new ProviderResponse { Status = 500, Error = "unexpected " + path };
        });
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "auth.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var outcome = await engine.SyncAsync(new ChatGptProvider(transport), AppSettings.CreateDefaults(), true);
        Assert.Equal(AppSyncStatus.AuthenticationRequired, outcome.Status);
        Assert.DoesNotContain(paths, path => path.Equals(ChatGptEndpoints.Me, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsupportedAccountsCheck_FallsBackToMe()
    {
        var paths = new List<string>();
        var transport = new PaginationTests.ScriptedTransport((_, path) =>
        {
            paths.Add(path);
            if (path == ChatGptEndpoints.Session)
            {
                return SessionOk();
            }

            if (path == ChatGptEndpoints.AccountsCheck)
            {
                return new ProviderResponse { Status = 404, Error = "not found" };
            }

            if (path == ChatGptEndpoints.Me)
            {
                return new ProviderResponse
                {
                    Status = 200,
                    Body = """{"email":"a@b.com","name":"A"}"""
                };
            }

            return new ProviderResponse { Status = 500, Error = "unexpected " + path };
        });

        var status = await new ChatGptProvider(transport).GetAccountStatusAsync();
        Assert.True(status.IsSignedIn);
        Assert.Equal("a@b.com", status.Email);
        Assert.Contains(ChatGptEndpoints.Me, paths);
        Assert.Contains(ChatGptEndpoints.AccountsCheck, paths);
    }

    [Fact]
    public async Task ForbiddenDuringBodyFetch_AbortsWithoutSessionExpiredLabel()
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(_ =>
            throw new ChatGptProviderException(CompanionDiagnostics.Forbidden403, 403));
        var outcome = await engine.SyncAsync(provider, settings, true);
        Assert.Equal(AppSyncStatus.Forbidden, outcome.Status);
        Assert.Equal(CompanionDiagnostics.Forbidden403, outcome.Detail);
        Assert.DoesNotContain("session expired", outcome.Detail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, provider.BodyFetches);
    }

    [Fact]
    public async Task MissingChatGptTab_IsNotOfflineOrSessionExpired()
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(_ =>
            throw new ChatGptProviderException(CompanionDiagnostics.NoChatGptTab, 0));
        var outcome = await engine.SyncAsync(provider, settings, true);
        Assert.Equal(AppSyncStatus.ChatGptTabRequired, outcome.Status);
        Assert.Equal(CompanionDiagnostics.NoChatGptTab, outcome.Detail);
        Assert.NotEqual(AppSyncStatus.Offline, outcome.Status);
        Assert.NotEqual(AppSyncStatus.AuthenticationRequired, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
    }

    [Fact]
    public async Task PageBridgeUnavailable_IsNotOffline()
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(_ =>
            throw new ChatGptProviderException(CompanionDiagnostics.PageBridgeUnavailable, 0));
        var outcome = await engine.SyncAsync(provider, settings, true);
        Assert.Equal(AppSyncStatus.PageBridgeUnavailable, outcome.Status);
        Assert.Equal(CompanionDiagnostics.PageBridgeUnavailable, outcome.Detail);
        Assert.NotEqual(AppSyncStatus.Offline, outcome.Status);
        Assert.Equal(1, provider.BodyFetches);
    }

    [Theory]
    [InlineData(CompanionBridgeProtocol.NotConnectedError, AppSyncStatus.CompanionDisconnected)]
    [InlineData(CompanionBridgeProtocol.DisconnectedError, AppSyncStatus.CompanionDisconnected)]
    [InlineData(CompanionBridgeProtocol.WriteFailedError, AppSyncStatus.BridgeWriteFailed)]
    public async Task LocalBridgeFailureDuringBodyFetch_IsNotOffline(string message, AppSyncStatus expected)
    {
        var (engine, provider, settings) = CreateTwoConversationHarness(_ =>
            throw new ChatGptProviderException(message, 0));
        var outcome = await engine.SyncAsync(provider, settings, true, new SyncRunOptions { Origin = SyncOrigin.Auto });
        Assert.Equal(expected, outcome.Status);
        Assert.NotEqual(AppSyncStatus.Offline, outcome.Status);
        Assert.Equal(DisplayFormatting.StatusLabel(expected), outcome.Detail);
        Assert.DoesNotContain("Offline", outcome.Detail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(UiText.ChatGptUnreachable, outcome.Detail ?? "", StringComparison.Ordinal);
        Assert.Equal(1, provider.BodyFetches);
    }

    private static ProviderResponse SessionOk() => new()
    {
        Status = 200,
        Body = """{"user":{"email":"a@b.com","id":"u1"}}"""
    };

    private static (SyncEngine Engine, ScriptedBodyProvider Provider, AppSettings Settings) CreateTwoConversationHarness(
        Func<string, ConversationLoadResult> load)
    {
        var dir = NewTempDir();
        var store = new SqliteStore(Path.Combine(dir, "fatal.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-a", UpdateTime = now, CreateTime = now - 10 }, ConversationFixtures.NormalPro("conv-a", now));
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-b", UpdateTime = now - 1, CreateTime = now - 20 }, ConversationFixtures.NormalPro("conv-b", now - 1));
        fixture.LoadOverride = load;
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        return (engine, new ScriptedBodyProvider(fixture), settings);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class ScriptedBodyProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;
        public int BodyFetches { get; private set; }

        public ScriptedBodyProvider(FixtureChatGptProvider inner) => _inner = inner;

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

    private sealed class ThrowingIndexProvider : IChatGptProvider
    {
        private readonly FixtureChatGptProvider _inner;
        public bool ThrowOnArchived { get; set; }

        public ThrowingIndexProvider(FixtureChatGptProvider inner) => _inner = inner;

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) => _inner.GetAccountStatusAsync(cancellationToken);
        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) => _inner.GetModelCatalogAsync(cancellationToken);
        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetConversationIndexAsync(archived, minUpdateTime, cancellationToken);
        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default)
        {
            if (ThrowOnArchived)
            {
                throw new ChatGptProviderException("Authentication required.", 401);
            }

            return _inner.GetArchivedConversationIndexAsync(minUpdateTime, cancellationToken);
        }
        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) => _inner.GetProjectsAsync(cancellationToken);
        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
            _inner.GetProjectConversationsAsync(projectId, minUpdateTime, cancellationToken);
        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
            _inner.GetConversationMessagesAsync(conversationId, cancellationToken);
        public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) => _inner.TryGetQuotaMetadataAsync(cancellationToken);
    }
}
