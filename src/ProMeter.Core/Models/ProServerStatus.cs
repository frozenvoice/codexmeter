namespace ProMeter.Models;

public sealed class ProModelLimit
{
    public string Slug { get; set; } = "";
    public DateTimeOffset? ResetAt { get; set; }
    public string? Description { get; set; }
}

public sealed class ProServerStatus
{
    public static readonly TimeSpan ResetCorrelationTolerance = TimeSpan.FromSeconds(2);

    public bool ServerObserved { get; set; }
    public ProRestrictionState RestrictionState { get; set; } = ProRestrictionState.Unknown;
    public DateTimeOffset? ResetAt { get; set; }
    public ServerResetConfidence ResetConfidence { get; set; } = ServerResetConfidence.None;
    public DateTimeOffset? ObservedAt { get; set; }
    public IReadOnlyList<ProModelLimit> ModelLimits { get; set; } = [];
    public string? CorrelatedBlockedFeatureName { get; set; }
    public int? CorrelatedBlockedFeatureLimitHint { get; set; }
    public string? BlockReason { get; set; }
    public string? RestrictionDescription { get; set; }
    public bool HasAmbiguousResets { get; set; }
    public bool Stale { get; set; }
    public DateTimeOffset? LastSuccessfulRefresh { get; set; }
    public DateTimeOffset? LastRefreshAttempt { get; set; }

    public static ProServerStatus Unknown() => new();

    public ProServerStatus AsStale()
    {
        var copy = Clone();
        copy.Stale = true;
        return copy;
    }

    public ProServerStatus Clone() => new()
    {
        ServerObserved = ServerObserved,
        RestrictionState = RestrictionState,
        ResetAt = ResetAt,
        ResetConfidence = ResetConfidence,
        ObservedAt = ObservedAt,
        ModelLimits = ModelLimits.Select(limit => new ProModelLimit
        {
            Slug = limit.Slug,
            ResetAt = limit.ResetAt,
            Description = limit.Description
        }).ToList(),
        CorrelatedBlockedFeatureName = CorrelatedBlockedFeatureName,
        CorrelatedBlockedFeatureLimitHint = CorrelatedBlockedFeatureLimitHint,
        BlockReason = BlockReason,
        RestrictionDescription = RestrictionDescription,
        HasAmbiguousResets = HasAmbiguousResets,
        Stale = Stale,
        LastSuccessfulRefresh = LastSuccessfulRefresh,
        LastRefreshAttempt = LastRefreshAttempt
    };
}
