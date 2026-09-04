using Microsoft.Data.Sqlite;

namespace ProMeter.Services;

public sealed class SqliteStore : IDisposable
{
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
        }
    }

    public ConversationRecord? GetConversation(string id)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT conversation_id, update_time, project_id, archived, last_scanned, last_seen_update_time, source FROM conversations WHERE conversation_id=$id";
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
            command.CommandText = "SELECT conversation_id, update_time, project_id, archived, last_scanned, last_seen_update_time, source FROM conversations";
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
            command.CommandText = """
                INSERT INTO conversations(conversation_id, update_time, project_id, archived, last_scanned, last_seen_update_time, source)
                VALUES($id,$update,$project,$archived,$scanned,$seen,$source)
                ON CONFLICT(conversation_id) DO UPDATE SET
                    update_time=excluded.update_time,
                    project_id=excluded.project_id,
                    archived=excluded.archived,
                    last_scanned=excluded.last_scanned,
                    last_seen_update_time=excluded.last_seen_update_time,
                    source=excluded.source;
                """;
            command.Parameters.AddWithValue("$id", record.ConversationId);
            command.Parameters.AddWithValue("$update", record.UpdateTime);
            command.Parameters.AddWithValue("$project", (object?)record.ProjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$archived", record.Archived ? 1 : 0);
            command.Parameters.AddWithValue("$scanned", (object?)record.LastScanned?.ToString("O") ?? DBNull.Value);
            command.Parameters.AddWithValue("$seen", record.LastSeenUpdateTime);
            command.Parameters.AddWithValue("$source", record.Source);
            command.ExecuteNonQuery();
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
                using var command = connection.CreateCommand();
                command.Transaction = tx;
                command.CommandText = """
                    INSERT INTO usage_events(
                        id, request_id, conversation_id, message_id, created_at, requested_model, response_model,
                        normalized_model, raw_model, reasoning_effort, source, project_id, is_archived,
                        first_seen_at, last_seen_at, quota_family, dedupe_key)
                    VALUES($id,$req,$conv,$msg,$created,$reqm,$resm,$norm,$raw,$effort,$source,$project,$archived,$first,$last,$family,$dedupe)
                    ON CONFLICT(dedupe_key) DO UPDATE SET
                        last_seen_at=excluded.last_seen_at,
                        requested_model=COALESCE(excluded.requested_model, usage_events.requested_model),
                        response_model=COALESCE(excluded.response_model, usage_events.response_model),
                        normalized_model=excluded.normalized_model,
                        raw_model=excluded.raw_model,
                        reasoning_effort=excluded.reasoning_effort,
                        project_id=COALESCE(excluded.project_id, usage_events.project_id),
                        is_archived=excluded.is_archived,
                        quota_family=excluded.quota_family;
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
                inserted += command.ExecuteNonQuery() > 0 ? 1 : 0;
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
                       first_seen_at, last_seen_at, quota_family, dedupe_key
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

    private static ConversationRecord ReadConversation(SqliteDataReader reader) => new()
    {
        ConversationId = reader.GetString(0),
        UpdateTime = reader.GetDouble(1),
        ProjectId = reader.IsDBNull(2) ? null : reader.GetString(2),
        Archived = reader.GetInt32(3) == 1,
        LastScanned = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        LastSeenUpdateTime = reader.GetDouble(5),
        Source = reader.IsDBNull(6) ? "chat" : reader.GetString(6)
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
        DedupeKey = reader.GetString(16)
    };
}
