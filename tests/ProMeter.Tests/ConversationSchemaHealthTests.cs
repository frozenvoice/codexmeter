using ProMeter.Codex;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ConversationSchemaHealthTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 6, 5, 20, 14, TimeSpan.Zero);

    [Theory]
    [InlineData(1, 0, 1, 0, 0, false)]
    [InlineData(2, 0, 2, 0, 0, false)]
    [InlineData(3, 0, 3, 0, 0, true)]
    [InlineData(5, 1, 4, 0, 0, false)]
    [InlineData(4, 0, 3, 1, 0, false)]
    [InlineData(3, 0, 0, 3, 0, false)]
    [InlineData(0, 0, 0, 0, 0, false)]
    [InlineData(3, 0, 3, 0, 1, false)]
    public void Policy_UsesConservativeAllSchemaThreshold(
        int attempted,
        int success,
        int schema,
        int timeout,
        int other,
        bool systemic)
    {
        var assessment = ConversationSchemaHealthPolicy.Evaluate(
            new ConversationSchemaHealthEvidence(attempted, success, schema, timeout, other));
        Assert.Equal(3, ConversationSchemaHealthPolicy.MinSystemicSchemaMismatchSamples);
        Assert.Equal(systemic, assessment.SystemicBreak);
    }

    [Fact]
    public async Task A_OneIsolatedSchemaMismatch_StaysPartialAndUsable()
    {
        var (engine, store, fixture, provider, settings, items) = CreateHarness(1);
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        var snapshot = Snapshot(engine, store, settings);
        Assert.True(snapshot.CurrentCycleKnown);
        Assert.False(snapshot.Coverage.ConversationSchemaSystemicFailure);
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.Usable, health.Kind);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.False(health.Actionable);
        Assert.Equal(items.Count, provider.BodyFetches);
    }

    [Fact]
    public async Task B_TwoSchemaMismatches_StayPartial()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(2);
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(2, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(2, provider.BodyFetches);
    }

    [Fact]
    public async Task C_ThreeSchemaMismatches_EscalateSystemicFailure()
    {
        var (engine, store, fixture, provider, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(0, engine.LastCoverage.LoadedConversations);
        var snapshot = Snapshot(engine, store, settings);
        Assert.True(snapshot.LastSync is not null);
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.NeedsAttention, health.DataStatusText);
        Assert.True(health.Actionable);
        var view = DataStatusPresentation.From(snapshot, AvailableCodex());
        Assert.Contains(UiText.RepeatedConversationSchemaMismatch, view.AdvancedLines, StringComparer.Ordinal);
        Assert.DoesNotContain(UiText.CoverageNoUserActionNote, view.AdvancedLines, StringComparer.Ordinal);
        Assert.DoesNotContain("conv-schema-0", string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);
    }

    [Fact]
    public async Task D_FourSchemaMismatchesWithOneSuccess_StayPartial()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(5);
        fixture.LoadOverride = id => id == "conv-schema-4"
            ? ConversationDetailLoader.FromFixture(UniquePro("conv-schema-4", T.AddHours(1).ToUnixTimeSeconds()))
            : SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(4, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(1, engine.LastCoverage.LoadedConversations);
        Assert.Equal(5, provider.BodyFetches);
    }

    [Fact]
    public async Task E_ThreeSchemaMismatchesPlusTimeout_StayPartial()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(4);
        fixture.LoadOverride = id => id == "conv-schema-3" ? TimeoutLoad() : SchemaLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
    }

    [Fact]
    public async Task F_ThreeTimeouts_AreNotSystemicSchemaBreak()
    {
        var (engine, _, fixture, provider, settings, _) = CreateHarness(3);
        fixture.LoadOverride = _ => TimeoutLoad();
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
    }

    [Fact]
    public async Task G_IndexSchemaMismatch_EscalatesImmediately()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "index.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var outcome = await engine.SyncAsync(new IndexMismatchProvider(), AppSettings.CreateDefaults(), true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.False(engine.LastCoverage.NormalChats);
    }

    [Fact]
    public async Task H_DuplicateNormalAndProjectAppearance_CountsOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "dup.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = T.AddHours(1).ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        var item = new ConversationIndexItem { Id = "conv-dup", UpdateTime = now, CreateTime = now - 10 };
        fixture.AddConversation(item, UniquePro("conv-dup", now));
        fixture.AddProject(
            new ProjectInfo { Id = "proj-dup" },
            [(new ConversationIndexItem { Id = "conv-dup", UpdateTime = now, CreateTime = now - 10 }, UniquePro("conv-dup", now))]);
        fixture.LoadOverride = _ => SchemaLoad();
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        var outcome = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(1, engine.LastCoverage.UniqueConversations);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
    }

    [Fact]
    public async Task I_DeferredHistoricalSchemaFailures_DoNotEscalate()
    {
        var clock = new MutableClock(T);
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "deferred.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var now = T.AddHours(1).ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        for (var i = 0; i < 3; i++)
        {
            var item = new ConversationIndexItem
            {
                Id = "conv-old-" + i,
                UpdateTime = now - i,
                CreateTime = now - 20 - i
            };
            fixture.AddConversation(item, UniquePro(item.Id, item.UpdateTime));
            store.RecordConversationFailure(
                null,
                item,
                ConversationScanStatus.SchemaMismatch,
                "mapping incomplete",
                ConversationFetchBackoff.SchemaMismatch,
                clock.UtcNow);
        }

        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        var outcome = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(0, provider.BodyFetches);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
    }

    [Fact]
    public async Task J_RecoveryAfterSystemicBreak_ClearsCurrentFailure()
    {
        var (engine, store, fixture, provider, settings, items) = CreateHarness(3);
        fixture.LoadOverride = _ => SchemaLoad();
        var run1 = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, run1.Status);
        Assert.True(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(UserFacingHealthKind.SyncFailed, UserFacingHealth.From(Snapshot(engine, store, settings)).Kind);

        foreach (var item in items)
        {
            item.UpdateTime += 90;
        }

        fixture.LoadOverride = id => ConversationDetailLoader.FromFixture(UniquePro(id, items[0].UpdateTime));
        var run2 = await engine.SyncAsync(provider, settings, force: false);
        Assert.NotEqual(AppSyncStatus.ProviderSchemaMismatch, run2.Status);
        Assert.False(engine.LastCoverage.ConversationSchemaSystemicFailure);
        Assert.Equal(0, engine.LastCoverage.FailedConversations);
        Assert.All(items, item =>
        {
            var record = store.GetConversation(item.Id);
            Assert.Equal(0, record?.ConsecutiveFetchFailures);
            Assert.Null(record?.LastFetchFailureCategory);
        });
        var health = UserFacingHealth.From(Snapshot(engine, store, settings));
        Assert.NotEqual(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.False(health.Actionable);
    }

    [Fact]
    public void SystemicFlag_LastSyncDoesNotHideBreak()
    {
        var coverage = new CoverageInfo
        {
            NormalIndexState = CollectionState.Complete,
            ConversationSchemaSystemicFailure = true,
            FailedConversations = 3,
            ConversationIncomplete = true
        };
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        var snapshot = new QuotaSnapshot
        {
            Used = 5,
            ReconstructedUsed = 5,
            CurrentCycleKnown = true,
            LastSync = DateTimeOffset.UtcNow,
            Status = AppSyncStatus.ProviderSchemaMismatch,
            Coverage = coverage
        };
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.NeedsAttention, health.DataStatusText);
        Assert.True(health.Actionable);
    }

    private static (SyncEngine Engine, SqliteStore Store, FixtureChatGptProvider Fixture, IncrementalSyncTests.CountingProvider Provider, AppSettings Settings, List<ConversationIndexItem> Items) CreateHarness(int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteStore(Path.Combine(dir, "schema.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var after = T.AddHours(1).ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider(quota: CycleQuota());
        var items = new List<ConversationIndexItem>();
        for (var i = 0; i < count; i++)
        {
            var item = new ConversationIndexItem
            {
                Id = "conv-schema-" + i,
                UpdateTime = after - i,
                CreateTime = after - 20 - i
            };
            items.Add(item);
            fixture.AddConversation(item, UniquePro(item.Id, item.UpdateTime));
        }

        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        return (engine, store, fixture, new IncrementalSyncTests.CountingProvider(fixture), settings, items);
    }

    private static ConversationLoadResult SchemaLoad() => new()
    {
        Complete = false,
        SchemaMismatch = true,
        FailureKind = ConversationLoadFailureKind.SchemaMismatch,
        Diagnostics = ["mapping incomplete"]
    };

    private static ConversationLoadResult TimeoutLoad() => new()
    {
        Complete = false,
        FailureKind = ConversationLoadFailureKind.Timeout,
        Diagnostics =
        [
            "paginated-head timed out",
            "full-mapping unavailable status=403",
            "legacy-mapping timed out"
        ]
    };

    private static QuotaSnapshot Snapshot(SyncEngine engine, SqliteStore store, AppSettings settings) =>
        new QuotaEngine().Build(
            store.GetUsageEvents(),
            settings,
            T.AddHours(12),
            engine.LastSyncCompleted,
            engine.LastCoverage,
            engine.LastQuotaMetadata,
            engine.LastStatus,
            engine.LastStatusDetail);

    private static JsonNode UniquePro(string conversationId, double time)
    {
        var json = ConversationFixtures.NormalPro(conversationId, time).ToJsonString()
            .Replace("req-normal", "req-" + conversationId, StringComparison.Ordinal);
        return JsonNode.Parse(json)!;
    }

    private static QuotaMetadataSet CycleQuota() => new()
    {
        ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.Unknown,
            LastConfirmedResetAt = T
        }
    };

    private static CodexQuotaSnapshot AvailableCodex() => new(
        CodexQuotaStatus.Available,
        null,
        DateTimeOffset.Now,
        DateTimeOffset.Now,
        null,
        null,
        null,
        [new CodexQuotaWindow(null, 97, 10080, null, CodexWindowKind.Weekly)],
        null);

    private sealed class IndexMismatchProvider : IChatGptProvider
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
