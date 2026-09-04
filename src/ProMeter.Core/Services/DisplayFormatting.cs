namespace ProMeter.Services;

public readonly record struct ResetDisplayInfo(
    string TimeLabel,
    string TimeValue,
    string? EstimateLabel,
    string? EstimateValue);

public static class DisplayFormatting
{
    public static string FormatStamp(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return UiText.IsKorean
            ? local.ToString("M월 d일 HH:mm", CultureInfo.GetCultureInfo("ko-KR"))
            : local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
    }

    public static string FormatDay(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return UiText.IsKorean
            ? local.ToString("M월 d일", CultureInfo.GetCultureInfo("ko-KR"))
            : local.ToString("MMM d", CultureInfo.InvariantCulture);
    }

    public static ResetDisplayInfo ResetDisplay(QuotaSnapshot snapshot)
    {
        if (snapshot.ResetAt is null)
        {
            return new ResetDisplayInfo(UiText.ResetTime, UiText.NotConfirmed, null, null);
        }

        var stamp = FormatStamp(snapshot.ResetAt.Value);
        return snapshot.ResetAnchorSource switch
        {
            ResetAnchorSource.Server => new ResetDisplayInfo(UiText.ResetTime, UiText.ResetServer(stamp), null, null),
            ResetAnchorSource.UserConfigured => new ResetDisplayInfo(UiText.ResetTime, UiText.ResetUserConfigured(stamp), null, null),
            _ => new ResetDisplayInfo(UiText.ResetTime, UiText.NotConfirmed, UiText.Estimate, stamp)
        };
    }

    public static string ResetLabel(QuotaSnapshot snapshot)
    {
        var display = ResetDisplay(snapshot);
        return display.EstimateValue is null
            ? display.TimeValue
            : $"{display.TimeValue} · {display.EstimateLabel} {display.EstimateValue}";
    }

    public static string RemainingDuration(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null || reset <= now)
        {
            return UiText.Soon;
        }

        var span = reset.Value - now;
        if (span.TotalDays >= 1)
        {
            return $"{(int)span.TotalDays}d {span.Hours}h";
        }

        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public static string LastSyncLabel(DateTimeOffset? lastSync)
    {
        if (lastSync is null)
        {
            return UiText.Never;
        }

        return lastSync.Value.ToLocalTime().ToString("HH:mm");
    }

    public static string StatusLabel(QuotaSnapshot snapshot) =>
        snapshot.IsSyncing ? UiText.SyncingEllipsis : StatusLabel(snapshot.Status);

    public static string StatusLabel(AppSyncStatus status) => status switch
    {
        AppSyncStatus.SignedOut => UiText.SignedOut,
        AppSyncStatus.AuthenticationRequired => UiText.AuthenticationRequired,
        AppSyncStatus.DetectingAccount => UiText.DetectingAccount,
        AppSyncStatus.LoadingCatalog => UiText.LoadingCatalog,
        AppSyncStatus.Syncing => UiText.Syncing,
        AppSyncStatus.UpToDate => UiText.UpToDate,
        AppSyncStatus.RateLimited => UiText.RateLimited,
        AppSyncStatus.ApiChanged => UiText.ApiChanged,
        AppSyncStatus.ProviderSchemaMismatch => UiText.ProviderSchemaMismatch,
        AppSyncStatus.PartialData => UiText.PartialData,
        AppSyncStatus.Offline => UiText.Offline,
        AppSyncStatus.Error => UiText.Error,
        AppSyncStatus.Forbidden => UiText.Forbidden403,
        AppSyncStatus.ChatGptTabRequired => UiText.NoChatGptTab,
        AppSyncStatus.PageBridgeUnavailable => UiText.PageBridgeUnavailable,
        _ => UiText.Idle
    };

    public static string UsageLabel(QuotaSnapshot snapshot)
    {
        var used = snapshot.DisplayUsageUnavailable
            ? "?"
            : snapshot.Used.ToString(CultureInfo.InvariantCulture);
        return $"{used} / {snapshot.Limit}";
    }

    public static string TrayIconText(QuotaSnapshot snapshot)
    {
        if (snapshot.DisplayUsageUnavailable)
        {
            return "?";
        }

        return snapshot.Remaining >= 100
            ? "99+"
            : snapshot.Remaining.ToString(CultureInfo.InvariantCulture);
    }

    public static string CountSourceLabel(QuotaSnapshot snapshot)
    {
        var label = snapshot.DisplayUsageUnavailable
            ? UiText.IncompleteReconstruction
            : snapshot.UsesServerCount
                ? UiText.ServerCount(snapshot.ReconstructedUsed)
                : snapshot.Coverage.CountConfidence == CoverageConfidence.HighConfidence
                    ? UiText.ReconstructedHigh
                    : UiText.ReconstructedEstimated;
        return snapshot.IsSyncing && snapshot.LastSync is not null
            ? $"{UiText.PreviousData} · {label}"
            : label;
    }

    public static string OverallCollectionLabel(CoverageInfo coverage) => coverage.OverallState switch
    {
        CollectionState.Complete => UiText.Complete,
        CollectionState.Partial => UiText.Partial,
        CollectionState.Estimated => UiText.Estimated,
        _ => UiText.Unavailable
    };

    public static string CollectionStateLabel(CollectionState state) => state switch
    {
        CollectionState.Complete => UiText.Complete,
        CollectionState.Partial => UiText.Partial,
        CollectionState.Failed => UiText.Failed,
        CollectionState.Estimated => UiText.Estimated,
        _ => UiText.Unavailable
    };

    public static string CoverageFlyoutValue(QuotaSnapshot snapshot)
    {
        if (snapshot.IsSyncing && snapshot.LastSync is null)
        {
            return UiText.SyncingEllipsis;
        }

        var label = OverallCollectionLabel(snapshot.Coverage);
        return snapshot.IsSyncing ? $"{label} · {UiText.PreviousData}" : label;
    }

    public static string ReasoningLimitValue(int? limit) =>
        limit is int value ? value.ToString(CultureInfo.InvariantCulture) : UiText.NotAvailable;

    public static string CountConfidenceLabel(CoverageConfidence confidence) => confidence switch
    {
        CoverageConfidence.Authoritative => UiText.CountConfidenceAuthoritative,
        CoverageConfidence.HighConfidence => UiText.CountConfidenceHigh,
        CoverageConfidence.Estimated => UiText.CountConfidenceEstimated,
        _ => UiText.CountConfidenceIncomplete
    };

    public static string Headline(QuotaSnapshot snapshot) =>
        snapshot.UsesServerCount
            ? UiText.GptProUsageServer(UsageLabel(snapshot), snapshot.ReconstructedUsed)
            : UiText.GptProUsage(UsageLabel(snapshot));

    public static string Tooltip(QuotaSnapshot snapshot)
    {
        var reset = ResetDisplay(snapshot);
        var resetLine = reset.EstimateValue is null
            ? $"{reset.TimeLabel}: {reset.TimeValue}"
            : $"{reset.TimeLabel}: {reset.TimeValue}\n{reset.EstimateLabel}: {reset.EstimateValue}";
        return $"""
            {UiText.ProductName}
            {UiText.GptPro}: {UsageLabel(snapshot)}
            {UiText.Remaining}: {(snapshot.DisplayUsageUnavailable ? "?" : snapshot.Remaining.ToString(CultureInfo.InvariantCulture))}
            {resetLine}
            {UiText.LastSync}: {LastSyncLabel(snapshot.LastSync)}
            {UiText.DataStatus}: {CoverageFlyoutValue(snapshot)}
            """;
    }
}
