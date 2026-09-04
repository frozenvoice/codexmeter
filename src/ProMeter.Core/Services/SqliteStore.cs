using Microsoft.Data.Sqlite;

namespace ProMeter.Services;

public sealed class SqliteStore : IDisposable
{
    private const string ConversationSelect = """
        SELECT conversation_id, update_time, project_id, archived, last_scanned, last_seen_update_time, source,
               last_successful_scan, last_error_at, last_error, scan_status
        FROM conversations
        """;

    private readonly string _connectionString;
    private readonly object _gate = new();

    public SqliteStore(string? path = null)
    {
        var dbPath = path ?? AppPaths.Database;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
    }

    public void Dispose()
    {
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS conversations (
                    conversation_id TEXT PRIMARY KEY,
                    update_time REAL NOT NULL DEFAULT 0,
                    project_id TEXT,
                    archived INTEGER NOT NULL DEFAULT 0,
                    last_scanned TEXT,
                    last_seen_update_time REAL NOT NULL DEFAULT 0,
                    source TEXT
                );

                CREATE TABLE IF NOT EXISTS usage_events (
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

                CREATE INDEX IF NOT EXISTS idx_usage_created ON usage_events(created_at);
                CREATE INDEX IF NOT EXISTS idx_usage_family ON usage_events(quota_family);
                CREATE INDEX IF NOT EXISTS idx_usage_conversation ON usage_events(conversation_id);

                CREATE TABLE IF NOT EXISTS sync_state (
                    key TEXT PRIMARY KEY,
                    value TEXT
                );

                CREATE TABLE IF NOT EXISTS observed_models (
                    slug TEXT PRIMARY KEY,
                    title TEXT,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL,
                    source TEXT
                );
                """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "conversations", "last_successful_scan", "TEXT");
            EnsureColumn(connection, "conversations", "last_error_at", "TEXT");
            EnsureColumn(connection, "conversations", "last_error", "TEXT");
            EnsureColumn(connection, "conversations", "scan_status", "TEXT");
            EnsureColumn(connection, "usage_events", "dedupe_confidence", "TEXT");
        }
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string type)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        alter.ExecuteNonQuery();
    }

    public ConversationRecord? GetConversation(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = ConversationSelect + " WHERE conversation_id=$id";
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadConversation(reader) : null;
        }
    }

    public IReadOnlyList<ConversationRecord> GetConversations()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = ConversationSelect;
            using var reader = command.ExecuteReader();
            var list = new List<ConversationRecord>();
            while (reader.Read())
            {
                list.Add(ReadConversation(reader));
            }

            return list;
        }
    }

    public void UpsertConversation(ConversationRecord record)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            BindConversation(command, record);
            command.ExecuteNonQuery();
        }
    }

    public void RecordConversationFailure(ConversationRecord? prior, ConversationIndexItem item, ConversationScanStatus status, string error)
    {
        var record = prior ?? new ConversationRecord { ConversationId = item.Id };
        record.ConversationId = item.Id;
        record.ProjectId = item.ProjectId ?? record.ProjectId;
        record.Archived = item.Archived;
        record.Source = string.IsNullOrWhiteSpace(item.Source) ? record.Source : item.Source;
        record.LastScanned = DateTimeOffset.UtcNow;
        record.LastErrorAt = DateTimeOffset.UtcNow;
        record.LastError = error;
        record.Status = status;
        UpsertConversation(record);
    }

    public void ReconcileConversation(ConversationRecord record, IReadOnlyList<UsageEvent> events)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var upsertConv = connection.CreateCommand();
            upsertConv.Transaction = tx;
            BindConversation(upsertConv, record);
            upsertConv.ExecuteNonQuery();

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var usage in events)
            {
                var key = string.IsNullOrWhiteSpace(usage.DedupeKey)
                    ? UsageEvent.BuildDedupeKey(usage.ConversationId, usage.RequestId, usage.MessageId)
                    : usage.DedupeKey;
                keys.Add(key);
                InsertEvent(connection, tx, usage);
            }

            using var stale = connection.CreateCommand();
            stale.Transaction = tx;
            if (keys.Count == 0)
            {
                stale.CommandText = """
                    DELETE FROM usage_events
                    WHERE conversation_id=$conv
                      AND source IN ('ConversationSync','ArchivedSync','ProjectSync')
                    """;
                stale.Parameters.AddWithValue("$conv", record.ConversationId);
            }
            else
            {
                stale.CommandText = """
                    DELETE FROM usage_events
                    WHERE conversation_id=$conv
                      AND source IN ('ConversationSync','ArchivedSync','ProjectSync')
                      AND dedupe_key NOT IN (SELECT value FROM json_each($keys))
                    """;
                stale.Parameters.AddWithValue("$conv", record.ConversationId);
                stale.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(keys));
            }

            stale.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public int UpsertUsageEvents(IEnumerable<UsageEvent> events)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            var inserted = 0;
            foreach (var e in events)
            {
                inserted += InsertEvent(connection, tx, e);
            }

            tx.Commit();
            return inserted;
        }
    }

    public IReadOnlyList<UsageEvent> GetUsageEvents()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, request_id, conversation_id, message_id, created_at, requested_model, response_model,
                       normalized_model, raw_model, reasoning_effort, source, project_id, is_archived,
                       first_seen_at, last_seen_at, quota_family, dedupe_key, dedupe_confidence
                FROM usage_events
                ORDER BY created_at ASC
                """;
            using var reader = command.ExecuteReader();
            var list = new List<UsageEvent>();
            while (reader.Read())
            {
                list.Add(ReadEvent(reader));
            }

            return list;
        }
    }

    public void SetState(string key, string? value)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO sync_state(key, value) VALUES($k,$v)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;
                """;
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    public string? GetState(string key)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM sync_state WHERE key=$k";
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar()?.ToString();
        }
    }

    public void ObserveModel(string slug, string? title, string source)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO observed_models(slug, title, first_seen_at, last_seen_at, source)
                VALUES($slug,$title,$now,$now,$source)
                ON CONFLICT(slug) DO UPDATE SET
                    title=COALESCE(excluded.title, observed_models.title),
                    last_seen_at=excluded.last_seen_at;
                """;
            var now = DateTimeOffset.UtcNow.ToString("O");
            command.Parameters.AddWithValue("$slug", slug);
            command.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$source", source);
            command.ExecuteNonQuery();
        }
    }

    private static void BindConversation(SqliteCommand command, ConversationRecord record)
    {
        command.CommandText = """
            INSERT INTO conversations(
                conversation_id, update_time, project_id, archived, last_scanned, last_seen_update_time, source,
                last_successful_scan, last_error_at, last_error, scan_status)
            VALUES($id,$update,$project,$archived,$scanned,$seen,$source,$success,$errat,$err,$status)
            ON CONFLICT(conversation_id) DO UPDATE SET
                update_time=CASE WHEN excluded.scan_status='Ok' THEN excluded.update_time ELSE conversations.update_time END,
                project_id=excluded.project_id,
                archived=excluded.archived,
                last_scanned=excluded.last_scanned,
                last_seen_update_time=CASE WHEN excluded.scan_status='Ok' THEN excluded.last_seen_update_time ELSE conversations.last_seen_update_time END,
                source=excluded.source,
                last_successful_scan=CASE WHEN excluded.scan_status='Ok' THEN excluded.last_successful_scan ELSE conversations.last_successful_scan END,
                last_error_at=excluded.last_error_at,
                last_error=excluded.last_error,
                scan_status=excluded.scan_status;
            """;
        command.Parameters.AddWithValue("$id", record.ConversationId);
        command.Parameters.AddWithValue("$update", record.UpdateTime);
        command.Parameters.AddWithValue("$project", (object?)record.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", record.Archived ? 1 : 0);
        command.Parameters.AddWithValue("$scanned", (object?)record.LastScanned?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$seen", record.LastSeenUpdateTime);
        command.Parameters.AddWithValue("$source", record.Source);
        command.Parameters.AddWithValue("$success", (object?)record.LastSuccessfulScan?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$errat", (object?)record.LastErrorAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$err", (object?)record.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", record.Status.ToString());
    }

    private static int InsertEvent(SqliteConnection connection, SqliteTransaction tx, UsageEvent e)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO usage_events(
                id, request_id, conversation_id, message_id, created_at, requested_model, response_model,
                normalized_model, raw_model, reasoning_effort, source, project_id, is_archived,
                first_seen_at, last_seen_at, quota_family, dedupe_key, dedupe_confidence)
            VALUES($id,$req,$conv,$msg,$created,$reqm,$resm,$norm,$raw,$effort,$source,$project,$archived,$first,$last,$family,$dedupe,$conf)
            ON CONFLICT(dedupe_key) DO UPDATE SET
                last_seen_at=excluded.last_seen_at,
                requested_model=COALESCE(excluded.requested_model, usage_events.requested_model),
                response_model=COALESCE(excluded.response_model, usage_events.response_model),
                normalized_model=excluded.normalized_model,
                raw_model=excluded.raw_model,
                reasoning_effort=excluded.reasoning_effort,
                project_id=COALESCE(excluded.project_id, usage_events.project_id),
                is_archived=excluded.is_archived,
                quota_family=excluded.quota_family,
                dedupe_confidence=excluded.dedupe_confidence;
            """;
        command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(e.Id) ? Guid.NewGuid().ToString("N") : e.Id);
        command.Parameters.AddWithValue("$req", (object?)e.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$conv", e.ConversationId);
        command.Parameters.AddWithValue("$msg", (object?)e.MessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", e.CreatedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$reqm", (object?)e.RequestedModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$resm", (object?)e.ResponseModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$norm", e.NormalizedModel);
        command.Parameters.AddWithValue("$raw", e.RawModel);
        command.Parameters.AddWithValue("$effort", ReasoningNormalizer.ToStorage(e.ReasoningEffort));
        command.Parameters.AddWithValue("$source", e.Source.ToString());
        command.Parameters.AddWithValue("$project", (object?)e.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", e.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$first", e.FirstSeenAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$last", e.LastSeenAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$family", e.QuotaFamily.ToString());
        command.Parameters.AddWithValue("$dedupe", string.IsNullOrWhiteSpace(e.DedupeKey)
            ? UsageEvent.BuildDedupeKey(e.ConversationId, e.RequestId, e.MessageId)
            : e.DedupeKey);
        command.Parameters.AddWithValue("$conf", e.DedupeConfidence.ToString());
        return command.ExecuteNonQuery() > 0 ? 1 : 0;
    }

    private static ConversationRecord ReadConversation(SqliteDataReader reader) => new()
    {
        ConversationId = reader.GetString(0),
        UpdateTime = reader.GetDouble(1),
        ProjectId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Archived = reader.GetInt32(3) == 1,
        LastScanned = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        LastSeenUpdateTime = reader.GetDouble(5),
        Source = reader.IsDBNull(6) ? "chat" : reader.GetString(6),
        LastSuccessfulScan = reader.FieldCount > 7 && !reader.IsDBNull(7) ? DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture) : null,
        LastErrorAt = reader.FieldCount > 8 && !reader.IsDBNull(8) ? DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture) : null,
        LastError = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null,
        Status = reader.FieldCount > 10 && !reader.IsDBNull(10) && Enum.TryParse<ConversationScanStatus>(reader.GetString(10), out var status)
            ? status
            : ConversationScanStatus.Unknown
    };

    private static UsageEvent ReadEvent(SqliteDataReader reader) => new()
    {
        Id = reader.GetString(0),
        RequestId = reader.IsDBNull(1) ? null : reader.GetString(1),
        ConversationId = reader.GetString(2),
        MessageId = reader.IsDBNull(3) ? null : reader.GetString(3),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        RequestedModel = reader.IsDBNull(5) ? null : reader.GetString(5),
        ResponseModel = reader.IsDBNull(6) ? null : reader.GetString(6),
        NormalizedModel = reader.IsDBNull(7) ? "" : reader.GetString(7),
        RawModel = reader.IsDBNull(8) ? "" : reader.GetString(8),
        ReasoningEffort = ReasoningNormalizer.FromStorage(reader.IsDBNull(9) ? null : reader.GetString(9)),
        Source = Enum.TryParse<UsageSource>(reader.IsDBNull(10) ? null : reader.GetString(10), out var source) ? source : UsageSource.ConversationSync,
        ProjectId = reader.IsDBNull(11) ? null : reader.GetString(11),
        IsArchived = reader.GetInt32(12) == 1,
        FirstSeenAt = DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
        LastSeenAt = DateTimeOffset.Parse(reader.GetString(14), CultureInfo.InvariantCulture),
        QuotaFamily = Enum.TryParse<QuotaFamily>(reader.IsDBNull(15) ? null : reader.GetString(15), out var family) ? family : QuotaFamily.Unknown,
        DedupeKey = reader.GetString(16),
        DedupeConfidence = reader.FieldCount > 17 && !reader.IsDBNull(17) && Enum.TryParse<DedupeConfidence>(reader.GetString(17), out var conf)
            ? conf
            : DedupeConfidence.High
    };
}
