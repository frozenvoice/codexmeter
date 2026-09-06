namespace ProMeter.Services;

public sealed class ProServerStatusStore
{
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _path;

    public ProServerStatusStore(string? path = null)
    {
        _path = path ?? AppPaths.ProServerStatus;
    }

    public ProServerStatus? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            if (ContainsForbiddenPayload(json))
            {
                return null;
            }

            var dto = JsonSerializer.Deserialize<PersistedStatus>(json, Options);
            return dto?.ToStatus();
        }
        catch
        {
            return null;
        }
    }

    public void Save(ProServerStatus status)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path) ?? ".");
        var json = JsonSerializer.Serialize(PersistedStatus.From(status), Options);
        if (ContainsForbiddenPayload(json))
        {
            throw new InvalidOperationException("Refusing to persist Pro server status that contains secrets.");
        }

        File.WriteAllText(_path, json);
    }

    public static bool ContainsForbiddenPayload(string json) =>
        json.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
        || json.Contains("access_token", StringComparison.OrdinalIgnoreCase)
        || json.Contains("refresh_token", StringComparison.OrdinalIgnoreCase)
        || json.Contains("\"cookie\"", StringComparison.OrdinalIgnoreCase)
        || json.Contains("@")
        || json.Contains("Bearer ", StringComparison.OrdinalIgnoreCase)
        || json.Contains("accountId", StringComparison.OrdinalIgnoreCase)
        || json.Contains("conversation_id", StringComparison.OrdinalIgnoreCase);

    private sealed class PersistedStatus
    {
        public int Version { get; set; } = CurrentVersion;
        public bool ServerObserved { get; set; }
        public string RestrictionState { get; set; } = nameof(ProRestrictionState.Unknown);
        public DateTimeOffset? ResetAt { get; set; }
        public string ResetConfidence { get; set; } = nameof(ServerResetConfidence.None);
        public DateTimeOffset? LastConfirmedResetAt { get; set; }
        public string? LastConfirmedResetAllowanceId { get; set; }
        public string LastConfirmedResetWindowKind { get; set; } = nameof(QuotaWindowKind.Unclassified);
        public DateTimeOffset? ObservedAt { get; set; }
        public List<PersistedLimit> ModelLimits { get; set; } = [];
        public string? CorrelatedBlockedFeatureName { get; set; }
        public int? CorrelatedBlockedFeatureLimitHint { get; set; }
        public string? BlockReason { get; set; }
        public string? RestrictionDescription { get; set; }
        public bool HasAmbiguousResets { get; set; }
        public bool Stale { get; set; }
        public DateTimeOffset? LastSuccessfulRefresh { get; set; }
        public DateTimeOffset? LastRefreshAttempt { get; set; }

        public static PersistedStatus From(ProServerStatus status) => new()
        {
            ServerObserved = status.ServerObserved,
            RestrictionState = status.RestrictionState.ToString(),
            ResetAt = status.ResetAt,
            ResetConfidence = status.ResetConfidence.ToString(),
            LastConfirmedResetAt = status.LastConfirmedResetAt,
            LastConfirmedResetAllowanceId = status.LastConfirmedResetAllowanceId,
            LastConfirmedResetWindowKind = status.LastConfirmedResetWindowKind.ToString(),
            ObservedAt = status.ObservedAt,
            ModelLimits = status.ModelLimits.Select(PersistedLimit.From).ToList(),
            CorrelatedBlockedFeatureName = status.CorrelatedBlockedFeatureName,
            CorrelatedBlockedFeatureLimitHint = status.CorrelatedBlockedFeatureLimitHint,
            BlockReason = status.BlockReason,
            RestrictionDescription = status.RestrictionDescription,
            HasAmbiguousResets = status.HasAmbiguousResets,
            Stale = status.Stale,
            LastSuccessfulRefresh = status.LastSuccessfulRefresh,
            LastRefreshAttempt = status.LastRefreshAttempt
        };

        public ProServerStatus ToStatus()
        {
            Enum.TryParse<ProRestrictionState>(RestrictionState, out var restriction);
            Enum.TryParse<ServerResetConfidence>(ResetConfidence, out var reset);
            var status = new ProServerStatus
            {
                ServerObserved = ServerObserved,
                RestrictionState = restriction,
                ResetAt = ResetAt,
                ResetConfidence = reset,
                LastConfirmedResetAt = LastConfirmedResetAt,
                LastConfirmedResetAllowanceId = LastConfirmedResetAllowanceId,
                LastConfirmedResetWindowKind = Enum.TryParse<QuotaWindowKind>(LastConfirmedResetWindowKind, out var kind)
                    ? kind
                    : QuotaWindowKind.Unclassified,
                ObservedAt = ObservedAt,
                ModelLimits = ModelLimits.Select(limit => limit.ToLimit()).ToList(),
                CorrelatedBlockedFeatureName = CorrelatedBlockedFeatureName,
                CorrelatedBlockedFeatureLimitHint = CorrelatedBlockedFeatureLimitHint,
                BlockReason = BlockReason,
                RestrictionDescription = RestrictionDescription,
                HasAmbiguousResets = HasAmbiguousResets,
                Stale = Stale,
                LastSuccessfulRefresh = LastSuccessfulRefresh,
                LastRefreshAttempt = LastRefreshAttempt
            };
            ProServerStatus.MigrateLoadedConfirmedReset(status);
            return status;
        }
    }

    private sealed class PersistedLimit
    {
        public string Slug { get; set; } = "";
        public DateTimeOffset? ResetAt { get; set; }

        public static PersistedLimit From(ProModelLimit limit) => new()
        {
            Slug = limit.Slug,
            ResetAt = limit.ResetAt
        };

        public ProModelLimit ToLimit() => new()
        {
            Slug = Slug,
            ResetAt = ResetAt
        };
    }
}
