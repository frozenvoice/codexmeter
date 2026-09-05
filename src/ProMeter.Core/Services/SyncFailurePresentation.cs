namespace ProMeter.Services;

public static class SyncFailurePresentation
{
    public static bool IsLoggedFailure(AppSyncStatus status) => status is
        AppSyncStatus.AuthenticationRequired
        or AppSyncStatus.SignedOut
        or AppSyncStatus.Offline
        or AppSyncStatus.Forbidden
        or AppSyncStatus.ChatGptTabRequired
        or AppSyncStatus.PageBridgeUnavailable
        or AppSyncStatus.ProviderSchemaMismatch
        or AppSyncStatus.RateLimited
        or AppSyncStatus.Error;

    public static bool ProducesSyncErrorToast(AppSyncStatus status) => status is
        AppSyncStatus.AuthenticationRequired
        or AppSyncStatus.SignedOut
        or AppSyncStatus.Offline
        or AppSyncStatus.Forbidden
        or AppSyncStatus.ChatGptTabRequired
        or AppSyncStatus.PageBridgeUnavailable
        or AppSyncStatus.Error;

    public static bool IsSuccessfulCompletion(AppSyncStatus status) =>
        status is AppSyncStatus.UpToDate or AppSyncStatus.PartialData;

    public static string LogLine(SyncOrigin origin, AppSyncStatus category) =>
        $"sync failed origin={FormatOrigin(origin)} category={category}";

    public static string FormatOrigin(SyncOrigin origin) => origin switch
    {
        SyncOrigin.Manual => "manual",
        SyncOrigin.Startup => "startup",
        SyncOrigin.FlyoutStaleRefresh => "flyout",
        _ => "auto"
    };
}

public readonly record struct SyncErrorToastState(
    string? Category,
    string? Detail,
    DateTimeOffset? LastNotifiedAt)
{
    public static SyncErrorToastState FromSettings(AppSettings settings)
    {
        DateTimeOffset? at = null;
        if (DateTimeOffset.TryParse(
                settings.LastSyncErrorToastAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            at = parsed;
        }

        return new(
            string.IsNullOrWhiteSpace(settings.LastSyncErrorToastCategory) ? null : settings.LastSyncErrorToastCategory,
            settings.LastSyncErrorToastDetail ?? "",
            at);
    }

    public void WriteTo(AppSettings settings)
    {
        settings.LastSyncErrorToastCategory = Category ?? "";
        settings.LastSyncErrorToastDetail = Detail ?? "";
        settings.LastSyncErrorToastAt = LastNotifiedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
    }

    public static void ClearFailureAttempt(AppSettings settings)
    {
        settings.LastSyncFailureCategory = "";
        settings.LastSyncFailureAt = "";
    }

    public static void RememberFailureAttempt(AppSettings settings, AppSyncStatus status, DateTimeOffset now)
    {
        settings.LastSyncFailureCategory = status.ToString();
        settings.LastSyncFailureAt = now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }
}

public static class SyncErrorNotificationGate
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ManualMinimumWindow = TimeSpan.FromMinutes(2);

    public static bool ShouldNotify(
        SyncErrorToastState state,
        AppSyncStatus status,
        string? detail,
        SyncOrigin origin,
        DateTimeOffset now,
        out SyncErrorToastState next)
    {
        if (!SyncFailurePresentation.ProducesSyncErrorToast(status))
        {
            next = state;
            return false;
        }

        var category = status.ToString();
        var normalized = NormalizeDetail(detail);
        var same = string.Equals(state.Category, category, StringComparison.Ordinal)
                   && string.Equals(state.Detail ?? "", normalized, StringComparison.Ordinal);
        if (!same)
        {
            next = new SyncErrorToastState(category, normalized, now);
            return true;
        }

        var elapsed = state.LastNotifiedAt is { } at ? now - at : TimeSpan.MaxValue;
        var window = origin == SyncOrigin.Manual ? ManualMinimumWindow : DuplicateWindow;
        if (elapsed >= window)
        {
            next = new SyncErrorToastState(category, normalized, now);
            return true;
        }

        next = state;
        return false;
    }

    public static SyncErrorToastState Cleared() => new(null, "", null);

    private static string NormalizeDetail(string? detail)
    {
        var sanitized = AppLog.Sanitize(detail);
        return sanitized.Length <= 160 ? sanitized : sanitized[..160];
    }
}

public static class FlyoutAutoSyncPolicy
{
    public static readonly TimeSpan FailedAttemptCooldown = TimeSpan.FromMinutes(2);

    public static bool AllowsManualRefresh => true;

    public static bool ShouldStartStaleAutoSync(
        bool autoSyncEnabled,
        bool fromTaskbarStrip,
        DateTimeOffset now,
        DateTimeOffset? lastSuccessfulSync,
        int syncIntervalMinutes,
        DateTimeOffset? lastFailedAttempt)
    {
        if (!autoSyncEnabled || fromTaskbarStrip)
        {
            return false;
        }

        if (lastFailedAttempt is { } failed && now - failed < FailedAttemptCooldown)
        {
            return false;
        }

        if (lastSuccessfulSync is null)
        {
            return true;
        }

        var interval = TimeSpan.FromMinutes(Math.Clamp(syncIntervalMinutes, 5, 180));
        return now - lastSuccessfulSync.Value > interval;
    }

    public static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
