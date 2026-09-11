using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CycleArc.Services;

/// <summary>
/// Outcome of inspecting the metering database before anything is allowed to migrate it.
/// </summary>
/// <param name="MigrationRequired">True when stored reconstruction semantics are older than the current build.</param>
/// <param name="SourceVersion">Reconstruction version observed before any write. -1 when there is no database yet.</param>
/// <param name="TargetVersion">Reconstruction version this build migrates to.</param>
/// <param name="BackupPath">Verified pre-migration backup, when one was needed and created.</param>
/// <param name="FailureReason">Set when migration is required but a verified backup could not be produced.</param>
public sealed record DatabaseMigrationPlan(
    bool MigrationRequired,
    int SourceVersion,
    int TargetVersion,
    string? BackupPath,
    string? FailureReason)
{
    /// <summary>The live database may only be opened for migration when this is true.</summary>
    public bool CanProceed => FailureReason is null;

    public bool BackupCreated => BackupPath is not null;
}

/// <summary>
/// Creates a rollback point before the first reconstruction-semantics write. Inspection never
/// mutates the source database and never runs application schema migrations, so a failed or
/// unverifiable backup leaves the user's data exactly as it was.
/// </summary>
public static class DatabaseMigrationBootstrap
{
    public const int MaxRetainedBackups = 3;

    private static readonly string[] CoreTables = ["conversations", "usage_events", "sync_state"];

    public static string BackupDirectory =>
        Directory.CreateDirectory(Path.Combine(AppPaths.Root, "backups", "metering")).FullName;

    /// <summary>
    /// Inspects the database, and when a reconstruction migration is required creates and verifies
    /// a SQLite-consistent backup. Returns without touching the source when no migration is needed.
    /// </summary>
    public static DatabaseMigrationPlan Prepare(
        string? databasePath = null,
        string? backupDirectory = null,
        Action<string>? log = null)
    {
        var target = ReconstructionSemantics.Version;
        var path = databasePath ?? AppPaths.Database;
        if (!File.Exists(path))
        {
            log?.Invoke("metering db bootstrap: no existing database");
            return new DatabaseMigrationPlan(false, -1, target, null, null);
        }

        int source;
        List<string> tables;
        try
        {
            using var inspect = OpenForInspection(path);
            tables = ReadTables(inspect);
            if (!tables.Contains("usage_events", StringComparer.Ordinal)
                && !tables.Contains("conversations", StringComparer.Ordinal))
            {
                log?.Invoke("metering db bootstrap: database has no reconstruction data yet");
                return new DatabaseMigrationPlan(false, -1, target, null, null);
            }

            source = ReadReconstructionVersion(inspect, tables);
        }
        catch (Exception ex)
        {
            return new DatabaseMigrationPlan(
                true,
                -1,
                target,
                null,
                "Existing metering database could not be inspected: " + AppLog.Sanitize(ex.Message));
        }

        if (source >= target)
        {
            log?.Invoke($"metering db bootstrap: reconstruction v{source} already current");
            return new DatabaseMigrationPlan(false, source, target, null, null);
        }

        var directory = backupDirectory ?? BackupDirectory;
        string destination;
        try
        {
            Directory.CreateDirectory(directory);
            destination = Path.Combine(directory, BackupFileName(source, target, DateTimeOffset.UtcNow));
            CreateConsistentBackup(path, destination);
        }
        catch (Exception ex)
        {
            return new DatabaseMigrationPlan(
                true,
                source,
                target,
                null,
                "Pre-migration backup could not be created: " + AppLog.Sanitize(ex.Message));
        }

        if (!TryVerifyBackup(destination, tables, source, out var verifyFailure))
        {
            return new DatabaseMigrationPlan(true, source, target, null, verifyFailure);
        }

        TrimOldBackups(directory, destination, log);
        log?.Invoke($"metering db bootstrap: verified backup for reconstruction v{source} -> v{target}");
        return new DatabaseMigrationPlan(true, source, target, destination, null);
    }

    /// <summary>Reads the stored reconstruction version without creating or migrating anything.</summary>
    public static int InspectReconstructionVersion(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return -1;
        }

        using var connection = OpenForInspection(databasePath);
        return ReadReconstructionVersion(connection, ReadTables(connection));
    }

    public static string BackupFileName(int sourceVersion, int targetVersion, DateTimeOffset now) =>
        string.Format(
            CultureInfo.InvariantCulture,
            LegacyInstallation.DatabaseBackupFormat,
            sourceVersion < 0 ? 0 : sourceVersion,
            targetVersion,
            now.ToUniversalTime().ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture));

    private static SqliteConnection OpenForInspection(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Copies through the SQLite backup API so committed WAL content is included. A plain file copy
    /// of the .db alone can miss committed pages that still live in the write-ahead log.
    /// </summary>
    private static void CreateConsistentBackup(string sourcePath, string destinationPath)
    {
        using var source = OpenForInspection(sourcePath);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static bool TryVerifyBackup(
        string destinationPath,
        IReadOnlyList<string> sourceTables,
        int sourceVersion,
        out string? failure)
    {
        try
        {
            using var connection = OpenForInspection(destinationPath);
            var copied = ReadTables(connection);
            foreach (var table in CoreTables)
            {
                if (sourceTables.Contains(table, StringComparer.Ordinal)
                    && !copied.Contains(table, StringComparer.Ordinal))
                {
                    failure = $"Pre-migration backup is missing the {table} table.";
                    return false;
                }
            }

            var copiedVersion = ReadReconstructionVersion(connection, copied);
            if (copiedVersion != sourceVersion)
            {
                failure = "Pre-migration backup reconstruction state does not match the source database.";
                return false;
            }

            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            failure = "Pre-migration backup could not be verified: " + AppLog.Sanitize(ex.Message);
            return false;
        }
    }

    private static List<string> ReadTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static int ReadReconstructionVersion(SqliteConnection connection, IReadOnlyList<string> tables)
    {
        if (!tables.Contains("sync_state", StringComparer.Ordinal))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM sync_state WHERE key=$key";
        command.Parameters.AddWithValue("$key", SqliteStore.ReconstructionSchemaStateKey);
        var value = command.ExecuteScalar()?.ToString();
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static void TrimOldBackups(string directory, string keepPath, Action<string>? log)
    {
        try
        {
            var stale = new DirectoryInfo(directory)
                .GetFiles(LegacyInstallation.DatabaseBackupPattern)
                .Where(file => !string.Equals(file.FullName, keepPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(Math.Max(0, MaxRetainedBackups - 1))
                .ToList();
            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("metering db bootstrap: backup trim skipped " + AppLog.Sanitize(ex.Message));
        }
    }
}
