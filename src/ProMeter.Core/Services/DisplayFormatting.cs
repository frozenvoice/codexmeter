namespace ProMeter.Services;

public static class DisplayFormatting
{
    public static string ResetLabel(QuotaSnapshot snapshot)
    {
        if (snapshot.ResetAt is null)
        {
            return "Unknown";
        }

        var local = snapshot.ResetAt.Value.ToLocalTime();
        var stamp = local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
        return snapshot.ResetAnchorSource switch
        {
            ResetAnchorSource.Server => $"{stamp} (server reset)",
            ResetAnchorSource.UserConfigured => $"{stamp} (user-configured reset)",
            _ => $"{stamp} (estimated reset)"
        };
    }

    public static string RemainingDuration(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null || reset <= now)
        {
            return "soon";
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
            return "Never";
        }

        return lastSync.Value.ToLocalTime().ToString("HH:mm");
    }

    public static string StatusLabel(AppSyncStatus status) => status switch
    {
        AppSyncStatus.SignedOut => "Signed out",
        AppSyncStatus.AuthenticationRequired => "Authentication required",
        AppSyncStatus.DetectingAccount => "Detecting account...",
        AppSyncStatus.LoadingCatalog => "Loading model catalog...",
        AppSyncStatus.Syncing => "Syncing",
        AppSyncStatus.UpToDate => "Up to date",
        AppSyncStatus.RateLimited => "Rate limited",
        AppSyncStatus.ApiChanged => "API changed",
        AppSyncStatus.ProviderSchemaMismatch => "Provider schema mismatch",
        AppSyncStatus.PartialData => "Partial data",
        AppSyncStatus.Offline => "Offline",
        AppSyncStatus.Error => "Error",
        _ => "Idle"
    };

    public static string UsageLabel(QuotaSnapshot snapshot)
    {
        var used = snapshot.DisplayUsageUnavailable
            ? "?"
            : snapshot.Used.ToString(CultureInfo.InvariantCulture);
        return $"{used} / {snapshot.Limit}";
    }

    public static string CountSourceLabel(QuotaSnapshot snapshot)
    {
        if (snapshot.DisplayUsageUnavailable)
        {
            return "Incomplete reconstruction";
        }

        if (snapshot.UsesServerCount)
        {
            return $"Server count · reconstructed {snapshot.ReconstructedUsed}";
        }

        return snapshot.Coverage.CountConfidence == CoverageConfidence.HighConfidence
            ? "Reconstructed · high confidence"
            : "Reconstructed · estimated";
    }

    public static string Headline(QuotaSnapshot snapshot) =>
        snapshot.UsesServerCount
            ? $"GPT Pro usage: {UsageLabel(snapshot)}  (server · reconstructed {snapshot.ReconstructedUsed})"
            : $"GPT Pro usage: {UsageLabel(snapshot)}";

    public static string Tooltip(QuotaSnapshot snapshot)
    {
        return $"""
            ProMeter
            GPT Pro: {UsageLabel(snapshot)}
            Remaining: {(snapshot.DisplayUsageUnavailable ? "?" : snapshot.Remaining.ToString(CultureInfo.InvariantCulture))}
            Reset: {ResetLabel(snapshot)}
            Last sync: {LastSyncLabel(snapshot.LastSync)}
            """;
    }
}
