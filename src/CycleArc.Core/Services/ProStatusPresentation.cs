namespace CycleArc.Services;

/// <summary>
/// The four mutually exclusive states GPT Pro reconstructed usage can be presented in.
/// Kept separate from any WPF code-behind so the metering semantics stay in one
/// place and are directly testable: "is the count usable" and "is the cycle boundary
/// confirmed" are independent questions that must not be conflated into a single
/// Unavailable/available toggle.
/// </summary>
public enum ReconstructionDisplayState
{
    /// <summary>Reconstruction itself is unusable (e.g. DisplayUsageUnavailable).</summary>
    Unavailable,
    /// <summary>Usable reconstruction, and the cycle/period boundary is known/confirmed.</summary>
    EstimatedKnownCycle,
    /// <summary>Usable reconstruction, but only an estimated fallback period boundary.</summary>
    EstimatedFallbackPeriod,
    /// <summary>Server-provided authoritative weekly count.</summary>
    AuthoritativeServerCount
}

public sealed class ProStatusPresentation
{
    public required string ProStateText { get; init; }
    public required string ProCompactToken { get; init; }
    public required string ResetText { get; init; }
    public required string ResetCompactTime { get; init; }
    public required string ResetLabel { get; init; }
    public required string ReconstructedText { get; init; }
    public required string ConfirmedRequestsText { get; init; }
    public required string ExactRemainingText { get; init; }
    public required string RestrictionDetail { get; init; }
    public required string HistoryLabel { get; init; }
    public required string DataStatusText { get; init; }
    public required string TrayIconGlyph { get; init; }
    public required string CountSourceText { get; init; }
    public required string HistoryLowerBoundCaption { get; init; }
    public required string Headline { get; init; }
    public required string ReconstructedLabel { get; init; }
    public ReconstructionDisplayState DisplayState { get; init; }
    public bool ExactRemainingAvailable { get; init; }
    public bool ShowHistoryLowerBound { get; init; }
    public bool Restricted { get; init; }
    public bool ServerStatusKnown { get; init; }
    public bool Stale { get; init; }
    public bool HasServerReset { get; init; }
    public bool ResetAmbiguous { get; init; }
    public DateTimeOffset? ServerResetAt { get; init; }
    public int UnresolvedCount { get; init; }

    /// <summary>
    /// Resolves which of the four reconstruction display states a snapshot is in.
    /// "Is the count usable" (DisplayUsageUnavailable) and "is the cycle boundary
    /// confirmed" (CurrentCycleKnown) are independent questions - a snapshot can have
    /// a perfectly usable reconstructed count for an estimated fallback period, and
    /// that must never collapse into Unavailable.
    /// </summary>
    public static ReconstructionDisplayState ResolveDisplayState(QuotaSnapshot snapshot)
    {
        if (snapshot.UsesServerWeeklyCount && !snapshot.DisplayUsageUnavailable)
        {
            return ReconstructionDisplayState.AuthoritativeServerCount;
        }

        if (snapshot.DisplayUsageUnavailable)
        {
            return ReconstructionDisplayState.Unavailable;
        }

        return snapshot.CurrentCycleKnown
            ? ReconstructionDisplayState.EstimatedKnownCycle
            : ReconstructionDisplayState.EstimatedFallbackPeriod;
    }

    public static ProStatusPresentation From(QuotaSnapshot snapshot)
    {
        var status = snapshot.ProServerStatus ?? ProServerStatus.Unknown();
        var restricted = status.RestrictionState == ProRestrictionState.CorrelatedRestriction;
        var known = status.ServerObserved && status.RestrictionState != ProRestrictionState.Unknown;
        var stale = status.Stale;
        var ambiguous = status.HasAmbiguousResets || status.ResetConfidence == ServerResetConfidence.Ambiguous;
        var hasServerReset = status.ResetConfidence == ServerResetConfidence.Server && status.ResetAt is not null;
        var displayState = ResolveDisplayState(snapshot);
        var exact = displayState == ReconstructionDisplayState.AuthoritativeServerCount;
        var reconstructed = FormatReconstructedCount(snapshot);
        var reconstructedLabel = displayState == ReconstructionDisplayState.EstimatedFallbackPeriod
            ? UiText.EstimatedPeriodReconstructed
            : UiText.CurrentCycleReconstructed;
        var stateText = status.RestrictionState switch
        {
            ProRestrictionState.CorrelatedRestriction => UiText.ProRestricted,
            ProRestrictionState.NoCorrelatedRestrictionObserved => UiText.ProNoServerRestriction,
            _ => UiText.Unavailable
        };
        if (stale && known)
        {
            stateText = $"{stateText} · {UiText.Stale}";
        }

        var compact = !known || status.RestrictionState == ProRestrictionState.Unknown
            ? "P?"
            : restricted
                ? "P!"
                : "POK";
        var resetTime = hasServerReset
            ? DisplayFormatting.FormatStamp(status.ResetAt!.Value)
            : ambiguous
                ? UiText.MultipleProResets
                : UiText.Unavailable;
        var resetCompact = hasServerReset
            ? status.ResetAt!.Value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
            : "";
        var remaining = exact
            ? snapshot.Remaining.ToString(CultureInfo.InvariantCulture)
            : UiText.ExactRemainingUnavailable;
        var glyph = exact
            ? DisplayFormatting.AuthoritativeRemainingGlyph(snapshot)
            : restricted
                ? "!"
                : known
                    ? "P"
                    : "?";
        var lowerBoundCaption = displayState == ReconstructionDisplayState.EstimatedFallbackPeriod
            ? UiText.EstimatedPeriodFooter
            : SupportsLowerBound(snapshot)
                ? UiText.HistoryBasedLowerBound
                : UiText.ReconstructedObservedCaption;
        var countSource = exact
            ? UiText.ServerCount(snapshot.ReconstructedUsed)
            : snapshot.DisplayUsageUnavailable
                ? UiText.IncompleteReconstruction
                : lowerBoundCaption;
        if (snapshot.IsSyncing && snapshot.LastSync is not null)
        {
            countSource = $"{UiText.PreviousData} · {countSource}";
        }

        var headline = exact
            ? UiText.GptProUsageServer($"{snapshot.Used} / {snapshot.Limit}", snapshot.ReconstructedUsed)
            : $"{UiText.GptPro}  {stateText}";

        return new ProStatusPresentation
        {
            ProStateText = stateText,
            ProCompactToken = compact,
            ResetText = resetTime,
            ResetCompactTime = resetCompact,
            ResetLabel = hasServerReset ? UiText.ServerReset : UiText.ServerReset,
            ReconstructedText = reconstructed,
            ConfirmedRequestsText = reconstructed,
            ExactRemainingText = remaining,
            ExactRemainingAvailable = exact,
            Restricted = restricted,
            ServerStatusKnown = known,
            Stale = stale,
            HasServerReset = hasServerReset,
            ResetAmbiguous = ambiguous,
            ServerResetAt = hasServerReset ? status.ResetAt : null,
            RestrictionDetail = restricted ? UiText.ProRestrictionMatchesReset : "",
            HistoryLabel = UiText.HistoryStatistics,
            DataStatusText = UserFacingHealth.From(snapshot).DataStatusText,
            TrayIconGlyph = glyph,
            CountSourceText = countSource,
            HistoryLowerBoundCaption = exact ? "" : lowerBoundCaption,
            Headline = headline,
            ShowHistoryLowerBound = !exact,
            UnresolvedCount = snapshot.UnresolvedCount,
            ReconstructedLabel = reconstructedLabel,
            DisplayState = displayState
        };
    }

    public static string FormatReconstructedCount(QuotaSnapshot snapshot)
    {
        var state = ResolveDisplayState(snapshot);
        if (state == ReconstructionDisplayState.Unavailable)
        {
            return "?";
        }

        if (state == ReconstructionDisplayState.EstimatedFallbackPeriod)
        {
            // The reset/cycle boundary is unconfirmed, but the reconstruction itself is
            // usable for the estimated fallback period - show it as an explicit estimate
            // rather than hiding a real, usable count behind "Unavailable".
            return UiText.ReconstructedCount(snapshot.ReconstructedUsed);
        }

        var n = snapshot.ReconstructedUsed.ToString(CultureInfo.InvariantCulture);
        return SupportsLowerBound(snapshot) ? n + "+" : UiText.ReconstructedCount(snapshot.ReconstructedUsed);
    }

    /// <summary>
    /// True when unresolved or legacy-migration evidence could still affect the current cycle's
    /// displayed count. The Flyout uses this only to show a short, number-free reliability note
    /// ("some history is being revalidated") — never the raw pending-row count.
    /// </summary>
    public static bool HasReliabilityConcern(QuotaSnapshot snapshot) =>
        snapshot.UnresolvedCount > 0 || snapshot.LegacyPendingCount > 0;

    public static bool SupportsLowerBound(QuotaSnapshot snapshot)
    {
        if (snapshot.ReconstructionIsLowerBound)
        {
            return true;
        }

        if (snapshot.UnresolvedCount > 0 || snapshot.HeuristicReconstructedCount > 0 || snapshot.DisplayUsageUnavailable)
        {
            return false;
        }

        return snapshot.Coverage.IndexIncomplete
               || snapshot.Coverage.ConversationIncomplete
               || snapshot.Coverage.FailedConversations > 0;
    }

    public static string CompactReconstructedToken(QuotaSnapshot snapshot)
    {
        var state = ResolveDisplayState(snapshot);
        if (state == ReconstructionDisplayState.Unavailable)
        {
            return "";
        }

        var n = snapshot.ReconstructedUsed.ToString(CultureInfo.InvariantCulture);
        if (state == ReconstructionDisplayState.EstimatedFallbackPeriod)
        {
            // Only the period boundary is estimated; never claim a mathematical lower
            // bound ("+") when the boundary itself isn't confirmed.
            return n + "~";
        }

        return SupportsLowerBound(snapshot) ? n + "+" : n + "~";
    }

    public string TrayTooltip(QuotaSnapshot snapshot)
    {
        var lines = new[]
        {
            UiText.ProductName,
            $"{UiText.GptPro}: {ProStateText}",
            HasServerReset ? $"{UiText.ServerReset}: {ResetText}" : null,
            $"{UiText.ExactRemaining}: {ExactRemainingText}",
            snapshot.IsSyncing
                ? DisplayFormatting.FlyoutHeader(snapshot)
                : snapshot.LastSync is DateTimeOffset
                    ? $"{UiText.LastSync}: {DisplayFormatting.LastSyncLabel(snapshot.LastSync)}"
                    : DisplayFormatting.FlyoutHeader(snapshot)
        };
        return NotifyIconText.Safe(string.Join("\n", lines.Where(line => !string.IsNullOrWhiteSpace(line))));
    }

    public string DetailedTooltip(QuotaSnapshot snapshot)
    {
        var resetLine = HasServerReset
            ? $"{UiText.ServerReset}: {ResetText}"
            : ResetAmbiguous
                ? $"{UiText.ServerReset}: {UiText.MultipleProResets}"
                : $"{UiText.ServerReset}: {UiText.Unavailable}";
        var block = string.IsNullOrWhiteSpace(snapshot.ProServerStatus?.BlockReason)
            ? null
            : $"{UiText.ServerReason}: {snapshot.ProServerStatus!.BlockReason}";
        return string.Join(
            Environment.NewLine,
            new[]
            {
                UiText.ProductName,
                $"{UiText.GptPro}: {ProStateText}",
                RestrictionDetail,
                resetLine,
                $"{UiText.ExactRemaining}: {ExactRemainingText}",
                $"{UiText.DataStatus}: {DataStatusText}",
                block,
                $"{UiText.LastSync}: {DisplayFormatting.LastSyncLabel(snapshot.LastSync)}"
            }.Where(line => !string.IsNullOrWhiteSpace(line)));
    }
}
