using Microsoft.Data.Sqlite;

namespace ProMeter.Services;

public sealed class SqliteStore : IDisposable
{
    private const string ConversationSelect = """
        SELECT conversation_id, update_time, project_id, archived, last_scanned, last_seen_update_time, source,
               last_successful_scan, last_error_at, last_error, scan_status,
               consecutive_fetch_failures, next_eligible_fetch_at, last_fetch_failure_category,
               last_attempted_update_time, fetch_failure_parser_version, reconstruction_version
        FROM conversations
        """;

    private readonly string _connectionString;
    private readonly object _gate = new();

    public SqliteStore(string? path = null, bool pooling = true)
    {
        var dbPath = path ?? AppPaths.Database;
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = pooling
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
            EnsureColumn(connection, "conversations", "consecutive_fetch_failures", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "conversations", "next_eligible_fetch_at", "TEXT");
            EnsureColumn(connection, "conversations", "last_fetch_failure_category", "TEXT");
            EnsureColumn(connection, "conversations", "last_attempted_update_time", "REAL NOT NULL DEFAULT 0");
            EnsureColumn(connection, "conversations", "fetch_failure_parser_version", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "usage_events", "dedupe_confidence", "TEXT");
            EnsureColumn(connection, "usage_events", "timestamp_provenance", "TEXT");
            EnsureColumn(connection, "usage_events", "model_confidence", "TEXT");
            EnsureColumn(connection, "usage_events", "period_ambiguous", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "usage_events", "countable", "INTEGER NOT NULL DEFAULT 1");
            EnsureColumn(connection, "usage_events", "unresolved_kind", "TEXT");
            EnsureColumn(connection, "usage_events", "reconstruction_version", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "usage_events", "request_started_at", "TEXT");
            EnsureColumn(connection, "usage_events", "response_completed_at", "TEXT");
            EnsureColumn(connection, "usage_events", "correction_reason", "TEXT");
            EnsureColumn(connection, "usage_events", "identity_aliases", "TEXT");
            EnsureColumn(connection, "conversations", "reconstruction_version", "INTEGER NOT NULL DEFAULT 0");
            using var extra = connection.CreateCommand();
            extra.CommandText = """
                CREATE TABLE IF NOT EXISTS usage_observations (
                    id TEXT PRIMARY KEY,
                    conversation_id TEXT NOT NULL,
                    message_id TEXT,
                    parent_message_id TEXT,
                    request_id TEXT,
                    role TEXT,
                    hidden INTEGER NOT NULL DEFAULT 0,
                    end_turn INTEGER,
                    recipient TEXT,
                    requested_model TEXT,
                    response_model TEXT,
                    raw_model TEXT,
                    reasoning_effort TEXT,
                    created_at TEXT,
                    source TEXT,
                    project_id TEXT,
                    is_archived INTEGER NOT NULL DEFAULT 0,
                    observed_at TEXT NOT NULL,
                    reconstruction_version INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(conversation_id, message_id)
                );
                """;
            extra.ExecuteNonQuery();
            extra.CommandText = "CREATE INDEX IF NOT EXISTS idx_obs_conversation ON usage_observations(conversation_id);";
            extra.ExecuteNonQuery();
            extra.CommandText = """
                CREATE TABLE IF NOT EXISTS usage_identity_aliases (
                    alias_key TEXT PRIMARY KEY,
                    canonical_dedupe_key TEXT NOT NULL
                );
                """;
            extra.ExecuteNonQuery();
            extra.CommandText = """
                CREATE TABLE IF NOT EXISTS metering_snapshots (
                    id TEXT PRIMARY KEY,
                    captured_at TEXT NOT NULL,
                    reconstruction_version INTEGER NOT NULL,
                    kind TEXT NOT NULL,
                    payload TEXT NOT NULL
                );
                """;
            extra.ExecuteNonQuery();
            MigrateReconstructionLedger(connection);
        }
    }

    public const string ReconstructionSchemaStateKey = "reconstruction_schema_version";

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

    public void RecordConversationFailure(
        ConversationRecord? prior,
        ConversationIndexItem item,
        ConversationScanStatus status,
        string error,
        string? category = null,
        DateTimeOffset? now = null,
        int httpStatus = 0)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var record = prior ?? new ConversationRecord { ConversationId = item.Id };
        record.ConversationId = item.Id;
        record.ProjectId = item.ProjectId ?? record.ProjectId;
        record.Archived = item.Archived;
        record.Source = string.IsNullOrWhiteSpace(item.Source) ? record.Source : item.Source;
        record.LastScanned = at;
        record.LastErrorAt = at;
        record.LastError = error;
        record.Status = status;
        record.ConsecutiveFetchFailures = Math.Max(0, record.ConsecutiveFetchFailures) + 1;
        record.LastFetchFailureCategory = ConversationFetchBackoff.NormalizeCategory(category, httpStatus);
        record.FetchFailureParserVersion = ConversationFetchBackoff.ParserCompatibilityVersion;
        if (item.UpdateTime > 0)
        {
            record.LastAttemptedUpdateTime = item.UpdateTime;
        }

        record.NextEligibleFetchAt = ConversationFetchBackoff.NextEligibleAt(at, record.ConsecutiveFetchFailures);
        UpsertConversation(record);
    }

    public void MergeConversationMetadata(string conversationId, string? projectId, bool archived, string? source)
    {
        lock (_gate)
        {
            var record = GetConversation(conversationId);
            if (record is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(record.ProjectId))
            {
                record.ProjectId = projectId;
            }

            if (archived)
            {
                record.Archived = true;
            }

            if (string.Equals(source, "project", StringComparison.Ordinal)
                && !string.Equals(record.Source, "project", StringComparison.Ordinal))
            {
                record.Source = "project";
            }

            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var upsert = connection.CreateCommand();
            upsert.Transaction = tx;
            BindConversation(upsert, record);
            upsert.ExecuteNonQuery();
            if (!string.IsNullOrWhiteSpace(projectId) || archived)
            {
                using var update = connection.CreateCommand();
                update.Transaction = tx;
                update.CommandText = """
                    UPDATE usage_events
                    SET project_id=COALESCE(project_id, $project),
                        is_archived=CASE WHEN $archived=1 THEN 1 ELSE is_archived END,
                        source=CASE WHEN $source='project' AND source IN ('ConversationSync','ArchivedSync') THEN 'ProjectSync' ELSE source END
                    WHERE conversation_id=$id
                    """;
                update.Parameters.AddWithValue("$project", (object?)projectId ?? DBNull.Value);
                update.Parameters.AddWithValue("$archived", archived ? 1 : 0);
                update.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
                update.Parameters.AddWithValue("$id", conversationId);
                update.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void ReconcileConversation(
        ConversationRecord record,
        IReadOnlyList<UsageEvent> events,
        IReadOnlyList<UsageObservation>? observations = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var upsertConv = connection.CreateCommand();
            upsertConv.Transaction = tx;
            BindConversation(upsertConv, record);
            upsertConv.ExecuteNonQuery();

            var incomingObservations = observations is { Count: > 0 }
                ? observations
                : events.Select(EventAsObservation).ToList();
            foreach (var observation in incomingObservations)
            {
                if (string.IsNullOrWhiteSpace(observation.ConversationId))
                {
                    observation.ConversationId = record.ConversationId;
                }

                InsertObservation(connection, tx, observation);
            }

            var existing = LoadEvents(connection, tx, record.ConversationId);
            var merged = RequestCanonicalizer.MergeCanonical(existing, events);
            var keep = new HashSet<string>(merged.Select(e => e.DedupeKey), StringComparer.OrdinalIgnoreCase);
            if (keep.Count > 0)
            {
                using var stale = connection.CreateCommand();
                stale.Transaction = tx;
                stale.CommandText = """
                    DELETE FROM usage_events
                    WHERE conversation_id=$conv
                      AND dedupe_key NOT IN (SELECT value FROM json_each($keys))
                    """;
                stale.Parameters.AddWithValue("$conv", record.ConversationId);
                stale.Parameters.AddWithValue("$keys", JsonSerializer.Serialize(keep));
                stale.ExecuteNonQuery();
            }
            foreach (var usage in merged)
            {
                InsertEvent(connection, tx, usage);
                RememberAliases(connection, tx, usage);
            }

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
                       first_seen_at, last_seen_at, quota_family, dedupe_key, dedupe_confidence,
                       timestamp_provenance, model_confidence, period_ambiguous, countable, unresolved_kind,
                       reconstruction_version, request_started_at, response_completed_at, correction_reason,
                       identity_aliases
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
                last_successful_scan, last_error_at, last_error, scan_status,
                consecutive_fetch_failures, next_eligible_fetch_at, last_fetch_failure_category,
                last_attempted_update_time, fetch_failure_parser_version, reconstruction_version)
            VALUES($id,$update,$project,$archived,$scanned,$seen,$source,$success,$errat,$err,$status,
                   $failures,$next,$failcat,$attempted,$parser,$recon)
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
                scan_status=excluded.scan_status,
                consecutive_fetch_failures=excluded.consecutive_fetch_failures,
                next_eligible_fetch_at=excluded.next_eligible_fetch_at,
                last_fetch_failure_category=excluded.last_fetch_failure_category,
                last_attempted_update_time=excluded.last_attempted_update_time,
                fetch_failure_parser_version=excluded.fetch_failure_parser_version,
                reconstruction_version=excluded.reconstruction_version;
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
        command.Parameters.AddWithValue("$failures", record.ConsecutiveFetchFailures);
        command.Parameters.AddWithValue("$next", (object?)record.NextEligibleFetchAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$failcat", (object?)record.LastFetchFailureCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$attempted", record.LastAttemptedUpdateTime);
        command.Parameters.AddWithValue("$parser", record.FetchFailureParserVersion);
        command.Parameters.AddWithValue("$recon", record.ReconstructionVersion);
    }

    private static int InsertEvent(SqliteConnection connection, SqliteTransaction tx, UsageEvent e)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO usage_events(
                id, request_id, conversation_id, message_id, created_at, requested_model, response_model,
                normalized_model, raw_model, reasoning_effort, source, project_id, is_archived,
                first_seen_at, last_seen_at, quota_family, dedupe_key, dedupe_confidence,
                timestamp_provenance, model_confidence, period_ambiguous, countable, unresolved_kind,
                reconstruction_version, request_started_at, response_completed_at, correction_reason,
                identity_aliases)
            VALUES($id,$req,$conv,$msg,$created,$reqm,$resm,$norm,$raw,$effort,$source,$project,$archived,$first,$last,$family,$dedupe,$conf,
                   $tprov,$mconf,$pamb,$countable,$unres,$rver,$rstart,$rend,$corr,$aliases)
            ON CONFLICT(dedupe_key) DO UPDATE SET
                last_seen_at=excluded.last_seen_at,
                request_id=COALESCE(excluded.request_id, usage_events.request_id),
                requested_model=COALESCE(excluded.requested_model, usage_events.requested_model),
                response_model=COALESCE(excluded.response_model, usage_events.response_model),
                normalized_model=CASE WHEN excluded.quota_family='Unknown' THEN usage_events.normalized_model ELSE excluded.normalized_model END,
                raw_model=COALESCE(NULLIF(excluded.raw_model,''), usage_events.raw_model),
                reasoning_effort=excluded.reasoning_effort,
                project_id=COALESCE(excluded.project_id, usage_events.project_id),
                is_archived=excluded.is_archived,
                quota_family=CASE WHEN excluded.quota_family='Unknown' THEN usage_events.quota_family ELSE excluded.quota_family END,
                dedupe_confidence=excluded.dedupe_confidence,
                timestamp_provenance=excluded.timestamp_provenance,
                model_confidence=excluded.model_confidence,
                period_ambiguous=excluded.period_ambiguous,
                countable=excluded.countable,
                unresolved_kind=excluded.unresolved_kind,
                reconstruction_version=excluded.reconstruction_version,
                request_started_at=COALESCE(excluded.request_started_at, usage_events.request_started_at),
                response_completed_at=COALESCE(excluded.response_completed_at, usage_events.response_completed_at),
                correction_reason=COALESCE(excluded.correction_reason, usage_events.correction_reason),
                identity_aliases=excluded.identity_aliases,
                created_at=CASE WHEN excluded.timestamp_provenance IN ('Unknown','LegacyUnverified') THEN usage_events.created_at ELSE excluded.created_at END;
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
        command.Parameters.AddWithValue("$tprov", e.TimestampProvenance.ToString());
        command.Parameters.AddWithValue("$mconf", e.ModelConfidence.ToString());
        command.Parameters.AddWithValue("$pamb", e.PeriodAmbiguous ? 1 : 0);
        command.Parameters.AddWithValue("$countable", e.Countable ? 1 : 0);
        command.Parameters.AddWithValue("$unres", e.UnresolvedKind.ToString());
        command.Parameters.AddWithValue("$rver", e.ReconstructionVersion);
        command.Parameters.AddWithValue("$rstart", (object?)e.RequestStartedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$rend", (object?)e.ResponseCompletedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$corr", (object?)e.CorrectionReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$aliases", (object?)e.IdentityAliases ?? DBNull.Value);
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
            : ConversationScanStatus.Unknown,
        ConsecutiveFetchFailures = ReadInt32(reader, 11),
        NextEligibleFetchAt = reader.FieldCount > 12 && !reader.IsDBNull(12)
            ? DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture)
            : null,
        LastFetchFailureCategory = reader.FieldCount > 13 && !reader.IsDBNull(13) ? reader.GetString(13) : null,
        LastAttemptedUpdateTime = reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetDouble(14) : 0,
        FetchFailureParserVersion = ReadInt32(reader, 15),
        ReconstructionVersion = ReadInt32(reader, 16)
    };

    private static int ReadInt32(SqliteDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return 0;
        }

        return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

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
            : DedupeConfidence.High,
        TimestampProvenance = ReadEnum(reader, 18, TimestampProvenance.Unspecified),
        ModelConfidence = ReadEnum(reader, 19, ModelEvidenceConfidence.Unspecified),
        PeriodAmbiguous = reader.FieldCount > 20 && !reader.IsDBNull(20) && Convert.ToInt32(reader.GetValue(20), CultureInfo.InvariantCulture) == 1,
        Countable = reader.FieldCount <= 21 || reader.IsDBNull(21) || Convert.ToInt32(reader.GetValue(21), CultureInfo.InvariantCulture) == 1,
        UnresolvedKind = ReadEnum(reader, 22, UnresolvedEvidenceKind.None),
        ReconstructionVersion = ReadInt32(reader, 23),
        RequestStartedAt = ReadTime(reader, 24),
        ResponseCompletedAt = ReadTime(reader, 25),
        CorrectionReason = reader.FieldCount > 26 && !reader.IsDBNull(26) ? reader.GetString(26) : null,
        IdentityAliases = reader.FieldCount > 27 && !reader.IsDBNull(27) ? reader.GetString(27) : null
    };

    private static T ReadEnum<T>(SqliteDataReader reader, int ordinal, T fallback) where T : struct, Enum
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return fallback;
        }

        return Enum.TryParse<T>(reader.GetString(ordinal), out var value) ? value : fallback;
    }

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return null;
        }

        return DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture);
    }

    private static void MigrateReconstructionLedger(SqliteConnection connection)
    {
        using var state = connection.CreateCommand();
        state.CommandText = "SELECT value FROM sync_state WHERE key=$k";
        state.Parameters.AddWithValue("$k", ReconstructionSchemaStateKey);
        var current = state.ExecuteScalar()?.ToString();
        if (string.Equals(current, ConversationFetchBackoff.ReconstructionSemanticsVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            return;
        }

        using var tx = connection.BeginTransaction();
        using var copy = connection.CreateCommand();
        copy.Transaction = tx;
        copy.CommandText = """
            INSERT OR IGNORE INTO usage_observations(
                id, conversation_id, message_id, request_id, requested_model, response_model, raw_model,
                reasoning_effort, created_at, source, project_id, is_archived, observed_at, reconstruction_version, role)
            SELECT id, conversation_id, message_id, request_id, requested_model, response_model, raw_model,
                   reasoning_effort, created_at, source, project_id, is_archived, first_seen_at, 0, 'assistant'
            FROM usage_events
            WHERE message_id IS NOT NULL;
            UPDATE usage_events SET reconstruction_version=0, timestamp_provenance='LegacyUnverified'
            WHERE reconstruction_version IS NULL OR reconstruction_version=0;
            INSERT INTO sync_state(key, value) VALUES($k,$v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        copy.Parameters.AddWithValue("$k", ReconstructionSchemaStateKey);
        copy.Parameters.AddWithValue("$v", ConversationFetchBackoff.ReconstructionSemanticsVersion.ToString(CultureInfo.InvariantCulture));
        copy.ExecuteNonQuery();
        tx.Commit();
    }

    private static List<UsageEvent> LoadEvents(SqliteConnection connection, SqliteTransaction tx, string conversationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT id, request_id, conversation_id, message_id, created_at, requested_model, response_model,
                   normalized_model, raw_model, reasoning_effort, source, project_id, is_archived,
                   first_seen_at, last_seen_at, quota_family, dedupe_key, dedupe_confidence,
                   timestamp_provenance, model_confidence, period_ambiguous, countable, unresolved_kind,
                   reconstruction_version, request_started_at, response_completed_at, correction_reason,
                   identity_aliases
            FROM usage_events
            WHERE conversation_id=$conv
            """;
        command.Parameters.AddWithValue("$conv", conversationId);
        using var reader = command.ExecuteReader();
        var list = new List<UsageEvent>();
        while (reader.Read())
        {
            list.Add(ReadEvent(reader));
        }

        return list;
    }

    private static void InsertObservation(SqliteConnection connection, SqliteTransaction tx, UsageObservation observation)
    {
        var messageId = observation.MessageId ?? observation.Id;
        if (string.IsNullOrWhiteSpace(messageId) || string.IsNullOrWhiteSpace(observation.ConversationId))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            INSERT INTO usage_observations(
                id, conversation_id, message_id, parent_message_id, request_id, role, hidden, end_turn, recipient,
                requested_model, response_model, raw_model, reasoning_effort, created_at, source, project_id,
                is_archived, observed_at, reconstruction_version)
            VALUES($id,$conv,$msg,$parent,$req,$role,$hidden,$end,$recip,$reqm,$resm,$raw,$effort,$created,$source,$project,$archived,$obs,$ver)
            ON CONFLICT(conversation_id, message_id) DO UPDATE SET
                parent_message_id=COALESCE(excluded.parent_message_id, usage_observations.parent_message_id),
                request_id=COALESCE(excluded.request_id, usage_observations.request_id),
                requested_model=COALESCE(excluded.requested_model, usage_observations.requested_model),
                response_model=COALESCE(excluded.response_model, usage_observations.response_model),
                raw_model=COALESCE(excluded.raw_model, usage_observations.raw_model),
                created_at=COALESCE(excluded.created_at, usage_observations.created_at),
                observed_at=excluded.observed_at,
                reconstruction_version=excluded.reconstruction_version;
            """;
        command.Parameters.AddWithValue("$id", observation.ConversationId + ":" + messageId);
        command.Parameters.AddWithValue("$conv", observation.ConversationId);
        command.Parameters.AddWithValue("$msg", messageId);
        command.Parameters.AddWithValue("$parent", (object?)observation.ParentMessageId ?? DBNull.Value);
        command.Parameters.AddWithValue("$req", (object?)observation.RequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", observation.Role);
        command.Parameters.AddWithValue("$hidden", observation.Hidden ? 1 : 0);
        command.Parameters.AddWithValue("$end", (object?)(observation.EndTurn is null ? DBNull.Value : observation.EndTurn.Value ? 1 : 0));
        command.Parameters.AddWithValue("$recip", (object?)observation.Recipient ?? DBNull.Value);
        command.Parameters.AddWithValue("$reqm", (object?)observation.RequestedModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$resm", (object?)observation.ResponseModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$raw", (object?)observation.RawModel ?? DBNull.Value);
        command.Parameters.AddWithValue("$effort", ReasoningNormalizer.ToStorage(observation.Effort));
        command.Parameters.AddWithValue("$created", (object?)observation.CreatedAt?.ToUniversalTime().ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", observation.Source.ToString());
        command.Parameters.AddWithValue("$project", (object?)observation.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$archived", observation.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$obs", observation.ObservedAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$ver", observation.ReconstructionVersion);
        command.ExecuteNonQuery();
    }

    private static void RememberAliases(SqliteConnection connection, SqliteTransaction tx, UsageEvent usage)
    {
        foreach (var alias in new[]
                 {
                     usage.DedupeKey,
                     usage.RequestId is null ? null : UsageEvent.ScopedRequestKey(usage.ConversationId, usage.RequestId),
                     usage.RequestId is null || string.IsNullOrWhiteSpace(usage.ConversationId)
                         ? null
                         : "legacy:" + usage.ConversationId + ":req:" + usage.RequestId,
                     usage.MessageId is null ? null : "msg:" + usage.ConversationId + ":" + usage.MessageId
                 }.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = """
                INSERT INTO usage_identity_aliases(alias_key, canonical_dedupe_key)
                VALUES($alias,$canon)
                ON CONFLICT(alias_key) DO UPDATE SET canonical_dedupe_key=excluded.canonical_dedupe_key;
                """;
            command.Parameters.AddWithValue("$alias", alias);
            command.Parameters.AddWithValue("$canon", usage.DedupeKey);
            command.ExecuteNonQuery();
        }
    }

    private static UsageObservation EventAsObservation(UsageEvent usage) => new()
    {
        Id = usage.Id,
        ConversationId = usage.ConversationId,
        MessageId = usage.MessageId ?? usage.Id,
        RequestId = usage.RequestId,
        Role = "assistant",
        RequestedModel = usage.RequestedModel,
        ResponseModel = usage.ResponseModel,
        RawModel = usage.RawModel,
        Effort = usage.ReasoningEffort,
        CreatedAt = usage.HasUsableTimestamp ? usage.CreatedAt : null,
        Source = usage.Source,
        ProjectId = usage.ProjectId,
        IsArchived = usage.IsArchived,
        ObservedAt = usage.LastSeenAt == default ? DateTimeOffset.UtcNow : usage.LastSeenAt,
        ReconstructionVersion = usage.ReconstructionVersion
    };

    public IReadOnlyList<(string Slug, string? Title)> GetObservedModels()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT slug, title FROM observed_models";
            using var reader = command.ExecuteReader();
            var list = new List<(string, string?)>();
            while (reader.Read())
            {
                list.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            return list;
        }
    }

    public IReadOnlyList<UsageObservation> GetObservations(string conversationId)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, conversation_id, message_id, parent_message_id, request_id, role, hidden, created_at, request_id
                FROM usage_observations WHERE conversation_id=$conv
                """;
            command.Parameters.AddWithValue("$conv", conversationId);
            using var reader = command.ExecuteReader();
            var list = new List<UsageObservation>();
            while (reader.Read())
            {
                list.Add(new UsageObservation
                {
                    Id = reader.GetString(0),
                    ConversationId = reader.GetString(1),
                    MessageId = reader.IsDBNull(2) ? null : reader.GetString(2),
                    ParentMessageId = reader.IsDBNull(3) ? null : reader.GetString(3),
                    RequestId = reader.IsDBNull(4) ? null : reader.GetString(4),
                    Role = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    Hidden = !reader.IsDBNull(6) && reader.GetInt32(6) == 1,
                    CreatedAt = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture)
                });
            }

            return list;
        }
    }

    public void BackupTo(string destinationPath)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var source = Open();
            using var dest = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
            dest.Open();
            source.BackupDatabase(dest);
        }
    }

    public void RecordMeteringSnapshot(string kind, object payload, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var insert = connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO metering_snapshots(id, captured_at, reconstruction_version, kind, payload)
                VALUES($id,$at,$ver,$kind,$payload);
                """;
            var captured = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
            insert.Parameters.AddWithValue("$at", captured.ToString("O"));
            insert.Parameters.AddWithValue("$ver", ConversationFetchBackoff.ReconstructionSemanticsVersion);
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(payload));
            insert.ExecuteNonQuery();
            using var trim = connection.CreateCommand();
            trim.Transaction = tx;
            trim.CommandText = """
                DELETE FROM metering_snapshots
                WHERE captured_at < COALESCE((
                    SELECT captured_at FROM metering_snapshots
                    ORDER BY captured_at DESC
                    LIMIT 1 OFFSET 39
                ), captured_at);
                """;
            trim.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public IReadOnlyList<string> GetMeteringSnapshotKinds()
    {
        lock (_gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind FROM metering_snapshots ORDER BY captured_at DESC";
            using var reader = command.ExecuteReader();
            var list = new List<string>();
            while (reader.Read())
            {
                list.Add(reader.GetString(0));
            }

            return list;
        }
    }
}
