namespace ProMeter.Services;

public static class ConversationFetchBackoff
{
    public const int ParserCompatibilityVersion = 3;
    public const string BodyTimeout = "BodyTimeout";
    public const string SchemaMismatch = "SchemaMismatch";
    public const string PayloadTooLarge = "PayloadTooLarge";
    public const string CompanionDisconnected = "CompanionDisconnected";
    public const string Authentication = "Authentication";
    public const string Other = "Other";

    public static TimeSpan DelayAfterFailures(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 0 => TimeSpan.Zero,
        1 => TimeSpan.FromMinutes(15),
        2 => TimeSpan.FromHours(1),
        3 => TimeSpan.FromHours(6),
        _ => TimeSpan.FromHours(24)
    };

    public static DateTimeOffset? NextEligibleAt(DateTimeOffset now, int consecutiveFailures) =>
        consecutiveFailures <= 0 ? null : now + DelayAfterFailures(consecutiveFailures);

    public static bool RemoteChanged(ConversationIndexItem item, ConversationRecord existing)
    {
        if (item.UpdateTime <= 0)
        {
            return false;
        }

        return item.UpdateTime > Math.Max(existing.LastSeenUpdateTime, existing.LastAttemptedUpdateTime) + 0.001;
    }

    public static bool ParserCompatibilityChanged(ConversationRecord existing) =>
        existing.ConsecutiveFetchFailures > 0
        && existing.FetchFailureParserVersion != ParserCompatibilityVersion;

    public static bool IsBackoffActive(ConversationRecord existing, DateTimeOffset now) =>
        existing.ConsecutiveFetchFailures > 0
        && existing.NextEligibleFetchAt is DateTimeOffset next
        && next > now;

    public static bool ShouldFetch(
        ConversationIndexItem item,
        ConversationRecord? existing,
        DateTimeOffset now,
        bool forceBodyRescan)
    {
        if (forceBodyRescan)
        {
            return true;
        }

        if (existing is null)
        {
            return true;
        }

        if (ParserCompatibilityChanged(existing))
        {
            return true;
        }

        if (RemoteChanged(item, existing))
        {
            return true;
        }

        if (item.UpdateTime <= 0)
        {
            return !IsBackoffActive(existing, now);
        }

        if (existing.Status == ConversationScanStatus.Ok && existing.LastSuccessfulScan is not null)
        {
            return false;
        }

        if (IsBackoffActive(existing, now))
        {
            return false;
        }

        return true;
    }

    public static bool IsDeferredFailure(
        ConversationIndexItem item,
        ConversationRecord? existing,
        DateTimeOffset now,
        bool forceBodyRescan)
    {
        if (forceBodyRescan || existing is null || existing.ConsecutiveFetchFailures <= 0)
        {
            return false;
        }

        return !ShouldFetch(item, existing, now, forceBodyRescan);
    }

    public static string NormalizeCategory(string? category, int status = 0)
    {
        if (string.Equals(category, BodyTimeout, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "BridgeTimeout", StringComparison.OrdinalIgnoreCase))
        {
            return BodyTimeout;
        }

        if (string.Equals(category, SchemaMismatch, StringComparison.OrdinalIgnoreCase))
        {
            return SchemaMismatch;
        }

        if (string.Equals(category, PayloadTooLarge, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "ResponseTooLarge", StringComparison.OrdinalIgnoreCase))
        {
            return PayloadTooLarge;
        }

        if (string.Equals(category, CompanionDisconnected, StringComparison.OrdinalIgnoreCase))
        {
            return CompanionDisconnected;
        }

        if (string.Equals(category, Authentication, StringComparison.OrdinalIgnoreCase)
            || string.Equals(category, "Forbidden", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(category, "HttpStatus", StringComparison.OrdinalIgnoreCase) && status is 401 or 403))
        {
            return Authentication;
        }

        return Other;
    }
}
