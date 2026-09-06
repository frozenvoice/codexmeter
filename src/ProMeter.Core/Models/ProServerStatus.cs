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
    public DateTimeOffset? LastConfirmedResetAt { get; set; }
    public string? LastConfirmedResetAllowanceId { get; set; }
    public QuotaWindowKind LastConfirmedResetWindowKind { get; set; }
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
        LastConfirmedResetAt = LastConfirmedResetAt,
        LastConfirmedResetAllowanceId = LastConfirmedResetAllowanceId,
        LastConfirmedResetWindowKind = LastConfirmedResetWindowKind,
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

    public static void RetainConfirmedReset(ProServerStatus incoming, ProServerStatus? previous)
    {
        if (incoming.ResetConfidence == ServerResetConfidence.Server && incoming.ResetAt is DateTimeOffset live)
        {
            incoming.LastConfirmedResetAt = live;
            incoming.LastConfirmedResetAllowanceId = incoming.LastConfirmedResetAllowanceId
                ?? incoming.LastConfirmedResetWindowKind switch
                {
                    QuotaWindowKind.Gpt6ProWeekly => "pro200-gpt6-weekly",
                    QuotaWindowKind.SharedProWeekly => "pro100-shared-weekly",
                    _ => incoming.LastConfirmedResetAllowanceId
                };
            return;
        }

        incoming.LastConfirmedResetAt = previous?.LastConfirmedResetAt ?? incoming.LastConfirmedResetAt;
        incoming.LastConfirmedResetAllowanceId = previous?.LastConfirmedResetAllowanceId ?? incoming.LastConfirmedResetAllowanceId;
        incoming.LastConfirmedResetWindowKind = previous is { LastConfirmedResetWindowKind: not QuotaWindowKind.Unclassified }
            ? previous.LastConfirmedResetWindowKind
            : incoming.LastConfirmedResetWindowKind;
    }

    public static void MigrateLoadedConfirmedReset(ProServerStatus status)
    {
        if (status.LastConfirmedResetAt is null
            && status.ResetConfidence == ServerResetConfidence.Server
            && status.ResetAt is DateTimeOffset live)
        {
            status.LastConfirmedResetAt = live;
        }
    }

    public static bool TryRecoverLastConfirmedFromRestrictionKey(ProServerStatus status, string? restrictionNotificationKey)
    {
        if (status.LastConfirmedResetAt is not null || string.IsNullOrWhiteSpace(restrictionNotificationKey))
        {
            return false;
        }

        var separator = restrictionNotificationKey.IndexOf(':');
        if (separator < 0 || separator == restrictionNotificationKey.Length - 1)
        {
            return false;
        }

        var stamp = restrictionNotificationKey[(separator + 1)..];
        if (!DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var recovered))
        {
            return false;
        }

        status.LastConfirmedResetAt = recovered;
        return true;
    }
}
