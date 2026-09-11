using Microsoft.Data.Sqlite;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// A reconstruction migration rewrites derived rows, so a verified rollback point must exist
/// before the auto-migrating store is opened. All paths here use temporary directories.
/// </summary>
public class DatabaseMigrationBootstrapTests
{
    [Fact]
    public void NoDatabase_NeedsNoBackup()
    {
        var dir = NewDir();
        var plan = DatabaseMigrationBootstrap.Prepare(
            Path.Combine(dir, "missing.db"),
            Path.Combine(dir, "backups"));

        Assert.False(plan.MigrationRequired);
        Assert.True(plan.CanProceed);
        Assert.Null(plan.BackupPath);
    }

    [Fact]
    public void WalBackedCommittedData_IsPresentInTheBackup()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "wal.db");

        // Hold the writer open so the committed rows stay in the write-ahead log and are not
        // yet checkpointed into the .db file a naive File.Copy would grab.
        using var writer = SeedLegacyOpen(path, proEvents: 3);
        Assert.True(File.Exists(path + "-wal"));
        Assert.True(new FileInfo(path + "-wal").Length > 0);

        var plan = DatabaseMigrationBootstrap.Prepare(path, Path.Combine(dir, "backups"));

        Assert.True(plan.MigrationRequired);
        Assert.True(plan.CanProceed);
        Assert.NotNull(plan.BackupPath);
        Assert.Equal(3, CountUsageEvents(plan.BackupPath!));
        Assert.Contains("pre-reconstruction-v0-to-v", Path.GetFileName(plan.BackupPath!), StringComparison.Ordinal);
        Assert.EndsWith(".db", plan.BackupPath, StringComparison.Ordinal);
    }

    [Fact]
    public void BackupIsCompletedAndVerifiedBeforeSchemaMigration()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "order.db");
        SeedLegacyDatabase(path, proEvents: 2);

        var plan = DatabaseMigrationBootstrap.Prepare(path, Path.Combine(dir, "backups"));

        // Bootstrap finished, yet the live DB is still on the old reconstruction version.
        Assert.NotNull(plan.BackupPath);
        Assert.True(File.Exists(plan.BackupPath));
        Assert.Equal(0, DatabaseMigrationBootstrap.InspectReconstructionVersion(path));
        Assert.Equal(0, ReadObservationCount(path));

        using (var store = new SqliteStore(path, pooling: false))
        {
            Assert.Equal(2, store.GetUsageEvents().Count);
        }

        Assert.Equal(ReconstructionSemantics.Version, DatabaseMigrationBootstrap.InspectReconstructionVersion(path));
        Assert.Equal(0, DatabaseMigrationBootstrap.InspectReconstructionVersion(plan.BackupPath!));
    }

    [Fact]
    public void UnverifiableBackupDestination_FailsClosedAndLeavesSourceUntouched()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "closed.db");
        SeedLegacyDatabase(path, proEvents: 1);
        var blockedDirectory = Path.Combine(dir, "blocked");

        // A file where the backup directory should be makes the backup impossible to create.
        File.WriteAllText(blockedDirectory, "not a directory");

        var plan = DatabaseMigrationBootstrap.Prepare(path, blockedDirectory);

        Assert.True(plan.MigrationRequired);
        Assert.False(plan.CanProceed);
        Assert.Null(plan.BackupPath);
        Assert.NotNull(plan.FailureReason);
        Assert.Equal(0, DatabaseMigrationBootstrap.InspectReconstructionVersion(path));
        Assert.Equal(1, CountUsageEvents(path));
    }

    [Fact]
    public void AlreadyCurrentDatabase_IsNotBackedUpAgain()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "current.db");
        var backups = Path.Combine(dir, "backups");
        SeedLegacyDatabase(path, proEvents: 1);

        var first = DatabaseMigrationBootstrap.Prepare(path, backups);
        Assert.NotNull(first.BackupPath);
        using (var store = new SqliteStore(path, pooling: false))
        {
            Assert.Single(store.GetUsageEvents());
        }

        var second = DatabaseMigrationBootstrap.Prepare(path, backups);
        Assert.False(second.MigrationRequired);
        Assert.Null(second.BackupPath);
        Assert.Single(Directory.GetFiles(backups, "prometer-pre-reconstruction-*.db"));
    }

    [Fact]
    public void OpeningMigratedDatabaseTwice_DoesNotDuplicateEvidence()
    {
        var dir = NewDir();
        var path = Path.Combine(dir, "twice.db");
        SeedLegacyDatabase(path, proEvents: 2);
        DatabaseMigrationBootstrap.Prepare(path, Path.Combine(dir, "backups"));

        int events;
        int observations;
        using (var first = new SqliteStore(path, pooling: false))
        {
            events = first.GetUsageEvents().Count;
            observations = first.GetObservations("conv-legacy").Count;
        }

        using var second = new SqliteStore(path, pooling: false);
        Assert.Equal(events, second.GetUsageEvents().Count);
        Assert.Equal(observations, second.GetObservations("conv-legacy").Count);
        Assert.Equal(2, events);
    }

    [Fact]
    public void BackupsAreBoundedButTheCurrentAttemptIsKept()
    {
        var dir = NewDir();
        var backups = Directory.CreateDirectory(Path.Combine(dir, "backups")).FullName;
        for (var i = 0; i < 5; i++)
        {
            var stale = Path.Combine(backups, $"prometer-pre-reconstruction-v0-to-v4-2026090{i}T000000Z.db");
            File.WriteAllText(stale, "old");
            File.SetLastWriteTimeUtc(stale, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i));
        }

        var path = Path.Combine(dir, "trim.db");
        SeedLegacyDatabase(path, proEvents: 1);

        var plan = DatabaseMigrationBootstrap.Prepare(path, backups);

        Assert.NotNull(plan.BackupPath);
        Assert.True(File.Exists(plan.BackupPath));
        var kept = Directory.GetFiles(backups, "prometer-pre-reconstruction-*.db");
        Assert.Equal(DatabaseMigrationBootstrap.MaxRetainedBackups, kept.Length);
        Assert.Contains(plan.BackupPath, kept);
    }

    [Fact]
    public void BackupFileName_CarriesVersionsAndUtcStampWithoutIdentifiers()
    {
        var name = DatabaseMigrationBootstrap.BackupFileName(0, 4, new DateTimeOffset(2026, 9, 6, 14, 23, 0, TimeSpan.Zero));
        Assert.Equal("prometer-pre-reconstruction-v0-to-v4-20260906T142300Z.db", name);
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Builds a pre-repair shaped database directly, without the auto-migrating store, and leaves
    /// the committed rows in a WAL file.
    /// </summary>
    private static void SeedLegacyDatabase(string path, int proEvents) =>
        SeedLegacyOpen(path, proEvents).Dispose();

    private static SqliteConnection SeedLegacyOpen(string path, int proEvents)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE conversations (
                conversation_id TEXT PRIMARY KEY,
                update_time REAL NOT NULL DEFAULT 0,
                project_id TEXT,
                archived INTEGER NOT NULL DEFAULT 0,
                last_scanned TEXT,
                last_seen_update_time REAL NOT NULL DEFAULT 0,
                source TEXT
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
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO conversations(conversation_id, update_time, last_seen_update_time, source)
            VALUES('conv-legacy', 100, 100, 'chat');
            """;
        command.ExecuteNonQuery();
        for (var i = 0; i < proEvents; i++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO usage_events(
                    id, request_id, conversation_id, message_id, created_at, normalized_model, raw_model,
                    source, first_seen_at, last_seen_at, quota_family, dedupe_key)
                VALUES($id,$req,'conv-legacy',$msg,$at,'GPT-6 Pro','gpt-6-pro','ConversationSync',$at,$at,'GptPro',$key);
                """;
            var stamp = new DateTimeOffset(2026, 9, 6, 6, i, 0, TimeSpan.Zero).ToString("O");
            insert.Parameters.AddWithValue("$id", "legacy-" + i);
            insert.Parameters.AddWithValue("$req", "req-legacy-" + i);
            insert.Parameters.AddWithValue("$msg", "msg-legacy-" + i);
            insert.Parameters.AddWithValue("$at", stamp);
            insert.Parameters.AddWithValue("$key", "req:req-legacy-" + i);
            insert.ExecuteNonQuery();
        }

        return connection;
    }

    private static int CountUsageEvents(string path) => ScalarInt(path, "SELECT COUNT(*) FROM usage_events");

    private static int ReadObservationCount(string path) => ScalarInt(
        path,
        "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='usage_observations'");

    private static int ScalarInt(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
