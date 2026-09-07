using Microsoft.Data.Sqlite;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

/// <summary>
/// Pre-repair derived rows keep their evidence but must not feed the reconstructed meter until
/// they are actually rebuilt under current semantics.
/// </summary>
public class LegacyUnverifiedEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LegacyRowsArePreservedButExcludedFromReconstructedUsed()
    {
        var (store, _) = OpenMigrated(legacyRequests: ["a"]);
        using var owned = store;

        var events = owned.GetUsageEvents();
        Assert.Single(events);
        Assert.Equal(UnresolvedEvidenceKind.LegacyUnverified, events[0].UnresolvedKind);
        Assert.False(events[0].Countable);
        Assert.Equal(0, events[0].ReconstructionVersion);
        Assert.True(QuotaEngine.IsLegacyUnverified(events[0]));

        var snapshot = Snapshot(owned);
        Assert.Equal(0, snapshot.ReconstructedUsed);
        Assert.Equal(1, snapshot.LegacyPendingCount);
        Assert.Equal(1, snapshot.UnresolvedCount);
        Assert.Equal(0, snapshot.TodayPro);
        Assert.Empty(snapshot.ModelBreakdown);
        Assert.Equal(UiText.UnresolvedPendingCount(1), UiText.UnresolvedPendingCount(snapshot.UnresolvedCount));
    }

    [Fact]
    public void LegacyEvidenceIsNotCalledZeroUsageAndIsNotDeleted()
    {
        var (store, path) = OpenMigrated(legacyRequests: ["a", "b"]);
        store.Dispose();

        using var reopened = new SqliteStore(path, pooling: false);
        Assert.Equal(2, reopened.GetUsageEvents().Count);
        var snapshot = Snapshot(reopened);
        Assert.Equal(0, snapshot.ReconstructedUsed);
        Assert.Equal(2, snapshot.LegacyPendingCount);
        Assert.False(snapshot.UsesServerWeeklyCount);
        Assert.Equal(UiText.ExactRemainingUnavailable, ProStatusPresentation.From(snapshot).ExactRemainingText);
    }

    [Fact]
    public async Task SuccessfulRevalidationPromotesLegacyEvidence()
    {
        var (store, path) = OpenMigrated(legacyRequests: ["legacy-0"]);
        store.Dispose();
        var harness = SyncHarness.Create(path, "conv-legacy", ConversationFixtures.NormalPro("conv-legacy", Now.AddHours(-2).ToUnixTimeSeconds()));

        Assert.Equal(1, Snapshot(harness.Store).LegacyPendingCount);

        var outcome = await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.UpToDate, outcome.Status);
        Assert.Equal(1, harness.Engine.LastReconstructionRevalidations);

        var events = harness.Store.GetUsageEvents();
        Assert.Single(events);
        Assert.Equal(ReconstructionSemantics.Version, events[0].ReconstructionVersion);
        Assert.Equal(UnresolvedEvidenceKind.None, events[0].UnresolvedKind);

        var snapshot = Snapshot(harness.Store);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        Assert.Equal(0, snapshot.LegacyPendingCount);
        harness.Dispose();
    }

    [Fact]
    public async Task LegacyDuplicateCandidatesRevalidateIntoOneRequest()
    {
        // Two pre-repair rows for the same generation: the tagged final and an untagged fragment.
        var (store, path) = OpenMigrated(legacyRequests: ["req-multi", "req-multi-analysis"]);
        store.Dispose();
        Assert.Equal(2, new SqliteStore(path, pooling: false).GetUsageEvents().Count);

        var harness = SyncHarness.Create(
            path,
            "conv-legacy",
            ConversationFixtures.MultiAssistantSameRequest("conv-legacy", Now.AddHours(-2).ToUnixTimeSeconds()));

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);

        var events = harness.Store.GetUsageEvents();
        Assert.Single(events);
        Assert.Equal("req-multi", events[0].RequestId);
        Assert.Equal(1, Snapshot(harness.Store).ReconstructedUsed);
        Assert.Equal(0, Snapshot(harness.Store).LegacyPendingCount);
        harness.Dispose();
    }

    [Fact]
    public async Task FailedRevalidationKeepsLegacyEvidencePendingWithoutFabricatingACount()
    {
        var (store, path) = OpenMigrated(legacyRequests: ["legacy-0"]);
        store.Dispose();
        var harness = SyncHarness.Create(path, "conv-legacy", ConversationFixtures.NormalPro("conv-legacy", Now.AddHours(-2).ToUnixTimeSeconds()));
        harness.Fixture.LoadOverride = _ => new ConversationLoadResult
        {
            Complete = false,
            FailureKind = ConversationLoadFailureKind.Timeout,
            Diagnostics = ["legacy-mapping timed out"]
        };

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);

        var events = harness.Store.GetUsageEvents();
        Assert.Single(events);
        Assert.Equal(0, events[0].ReconstructionVersion);
        Assert.Equal(UnresolvedEvidenceKind.LegacyUnverified, events[0].UnresolvedKind);

        var snapshot = Snapshot(harness.Store);
        Assert.Equal(0, snapshot.ReconstructedUsed);
        Assert.Equal(1, snapshot.LegacyPendingCount);
        Assert.Equal(0, harness.Store.GetConversation("conv-legacy")!.ReconstructionVersion);
        harness.Dispose();
    }

    private static QuotaSnapshot Snapshot(SqliteStore store) =>
        new QuotaEngine().Build(
            store.GetUsageEvents(),
            Settings(),
            Now,
            Now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet(),
            AppSyncStatus.UpToDate);

    private static AppSettings Settings()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.ResetWeekday = DayOfWeek.Monday;
        settings.ResetAnchorConfigured = true;
        settings.BodyFetchDelayMilliseconds = 0;
        return settings;
    }

    private static (SqliteStore Store, string Path) OpenMigrated(string[] legacyRequests)
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "legacy.db");
        SeedPreRepairDatabase(path, legacyRequests);
        var plan = DatabaseMigrationBootstrap.Prepare(path, Path.Combine(dir, "backups"));
        Assert.True(plan.CanProceed);
        Assert.True(plan.MigrationRequired);
        return (new SqliteStore(path, pooling: false), path);
    }

    /// <summary>Writes rows shaped like the pre-repair schema, without the auto-migrating store.</summary>
    private static void SeedPreRepairDatabase(string path, string[] requestIds)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE conversations (
                conversation_id TEXT PRIMARY KEY,
                update_time REAL NOT NULL DEFAULT 0,
                project_id TEXT,
                archived INTEGER NOT NULL DEFAULT 0,
                last_scanned TEXT,
                last_seen_update_time REAL NOT NULL DEFAULT 0,
                source TEXT,
                last_successful_scan TEXT,
                last_error_at TEXT,
                last_error TEXT,
                scan_status TEXT,
                consecutive_fetch_failures INTEGER NOT NULL DEFAULT 0,
                next_eligible_fetch_at TEXT,
                last_fetch_failure_category TEXT,
                last_attempted_update_time REAL NOT NULL DEFAULT 0,
                fetch_failure_parser_version INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE usage_events (
                id TEXT PRIMARY KEY,
                request_id TEXT,
                conversation_id TEXT NOT NULL,
                message_id TEXT,
                created_at TEXT NOT NULL,
                requested_model TEXT,
                response_model TEXT,
                normalized_model TEXT,
                raw_model TEXT,
                reasoning_effort TEXT,
                source TEXT,
                project_id TEXT,
                is_archived INTEGER NOT NULL DEFAULT 0,
                first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                quota_family TEXT,
                dedupe_key TEXT NOT NULL UNIQUE
            );
            CREATE TABLE sync_state (key TEXT PRIMARY KEY, value TEXT);
            """;
        schema.ExecuteNonQuery();

        var updateTime = Now.AddHours(-2).ToUnixTimeSeconds();
        using var conv = connection.CreateCommand();
        conv.CommandText = """
            INSERT INTO conversations(
                conversation_id, update_time, last_seen_update_time, last_scanned, source,
                last_successful_scan, scan_status, last_attempted_update_time)
            VALUES('conv-legacy', $t, $t, $scanned, 'chat', $scanned, 'Ok', $t);
            """;
        conv.Parameters.AddWithValue("$t", (double)updateTime);
        conv.Parameters.AddWithValue("$scanned", Now.AddHours(-1).ToString("O"));
        conv.ExecuteNonQuery();

        for (var i = 0; i < requestIds.Length; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO usage_events(
                    id, request_id, conversation_id, message_id, created_at, normalized_model, raw_model,
                    source, first_seen_at, last_seen_at, quota_family, dedupe_key)
                VALUES($id,$req,'conv-legacy',$msg,$at,'GPT-5.6 Sol Pro','gpt-5-6-pro','ConversationSync',$at,$at,'GptPro',$key);
                """;
            var at = Now.AddHours(-3).AddMinutes(i).ToString("O");
            insert.Parameters.AddWithValue("$id", "legacy-" + i);
            insert.Parameters.AddWithValue("$req", requestIds[i]);
            insert.Parameters.AddWithValue("$msg", "asst-" + i);
            insert.Parameters.AddWithValue("$at", at);
            insert.Parameters.AddWithValue("$key", "req:" + requestIds[i]);
            insert.ExecuteNonQuery();
        }
    }

    private sealed record SyncHarness(
        SyncEngine Engine,
        SqliteStore Store,
        FixtureChatGptProvider Fixture,
        IncrementalSyncTests.CountingProvider Provider,
        AppSettings Settings) : IDisposable
    {
        public static SyncHarness Create(string databasePath, string conversationId, JsonNode body)
        {
            var store = new SqliteStore(databasePath, pooling: false);
            var stored = store.GetConversation(conversationId)!;

            // The remote conversation is unchanged; only the reconstruction version is old.
            var item = new ConversationIndexItem
            {
                Id = conversationId,
                UpdateTime = stored.UpdateTime,
                CreateTime = stored.UpdateTime - 10
            };
            var fixture = new FixtureChatGptProvider();
            fixture.AddConversation(item, body);
            var models = new ModelNormalizer();
            var engine = new SyncEngine(
                store,
                new ConversationParser(models, new MutableClock(Now)),
                models,
                new AppLog(Path.Combine(Path.GetDirectoryName(databasePath)!, "logs")),
                new MutableClock(Now));
            return new SyncHarness(
                engine,
                store,
                fixture,
                new IncrementalSyncTests.CountingProvider(fixture),
                LegacyUnverifiedEvidenceTests.Settings());
        }

        public void Dispose() => Store.Dispose();
    }
}
