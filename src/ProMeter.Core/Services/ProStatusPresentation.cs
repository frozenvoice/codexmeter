namespace ProMeter.Services;

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
    public required string Headline { get; init; }
    public bool ExactRemainingAvailable { get; init; }
    public bool Restricted { get; init; }
    public bool ServerStatusKnown { get; init; }
    public bool Stale { get; init; }
    public bool HasServerReset { get; init; }
    public bool ResetAmbiguous { get; init; }
    public DateTimeOffset? ServerResetAt { get; init; }

    public static ProStatusPresentation From(QuotaSnapshot snapshot)
    {
        var status = snapshot.ProServerStatus ?? ProServerStatus.Unknown();
        var restricted = status.RestrictionState == ProRestrictionState.CorrelatedRestriction;
        var known = status.ServerObserved && status.RestrictionState != ProRestrictionState.Unknown;
        var stale = status.Stale;
        var ambiguous = status.HasAmbiguousResets || status.ResetConfidence == ServerResetConfidence.Ambiguous;
        var hasServerReset = status.ResetConfidence == ServerResetConfidence.Server && status.ResetAt is not null;
        var exact = snapshot.UsesServerWeeklyCount && !snapshot.DisplayUsageUnavailable;
        var reconstructed = snapshot.DisplayUsageUnavailable
            ? "?"
            : snapshot.ReconstructedUsed.ToString(CultureInfo.InvariantCulture) + "+";
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
        var countSource = exact
            ? UiText.ServerCount(snapshot.ReconstructedUsed)
            : snapshot.DisplayUsageUnavailable
                ? UiText.IncompleteReconstruction
                : UiText.ReconstructedHistory;
        if (snapshot.IsSyncing && snapshot.LastSync is not null)
        {
            countSource = $"{UiText.PreviousData} · {countSource}";
        }

        var headline = exact
            ? UiText.GptProUsageServer($"{snapshot.Used} / {snapshot.Limit}", snapshot.ReconstructedUsed)
            : UiText.GptProConfirmedHeadline(reconstructed);

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
            DataStatusText = DisplayFormatting.CoverageFlyoutValue(snapshot),
            TrayIconGlyph = glyph,
            CountSourceText = countSource,
            Headline = headline
        };
    }

    public string TrayTooltip(QuotaSnapshot snapshot)
    {
        var lines = new[]
        {
            UiText.ProductName,
            $"{UiText.GptPro}: {ProStateText}",
            HasServerReset ? $"{UiText.ServerReset}: {ResetText}" : null,
            $"{UiText.ExactRemaining}: {ExactRemainingText}",
            $"{UiText.ConfirmedRequests}: {ReconstructedText}",
            snapshot.IsSyncing
                ? DisplayFormatting.StatusLabel(snapshot)
                : snapshot.LastSync is DateTimeOffset
                    ? $"{UiText.LastSync}: {DisplayFormatting.LastSyncLabel(snapshot.LastSync)}"
                    : DisplayFormatting.StatusLabel(snapshot)
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
                $"{UiText.ConfirmedRequests}: {ReconstructedText}",
                $"{UiText.DataStatus}: {DataStatusText}",
                block,
                $"{UiText.LastSync}: {DisplayFormatting.LastSyncLabel(snapshot.LastSync)}"
            }.Where(line => !string.IsNullOrWhiteSpace(line)));
    }
}
