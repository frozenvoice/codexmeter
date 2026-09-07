namespace ProMeter.Services;

public readonly record struct ProQuotaPeriodResolution(
    DateTimeOffset Start,
    DateTimeOffset End,
    ResetAnchorSource Source,
    bool CurrentCycleKnown,
    bool NextResetEstimated);

public static class ProQuotaPeriodResolver
{
    public const string Pro100SharedWeekly = "pro100-shared-weekly";
    public const string Pro200Gpt6Weekly = "pro200-gpt6-weekly";

    public static ProQuotaPeriodResolution Resolve(
        AppSettings settings,
        DateTimeOffset now,
        ProServerStatus? proStatus,
        DateTimeOffset? weeklyReset = null,
        QuotaMetadataSet? metadata = null)
    {
        var weekly = SelectWeeklyReset(settings, now, proStatus, weeklyReset, metadata);
        if (weekly is { } live)
        {
            var (start, end) = QuotaPeriodCalculator.PeriodContaining(now, live.ResetAt);
            return new ProQuotaPeriodResolution(
                start,
                end,
                live.Source,
                true,
                now >= live.ResetAt || live.Source != ResetAnchorSource.Server);
        }

        var fallback = QuotaPeriodCalculator.CurrentPeriod(settings, now);
        if (settings.ResetAnchorConfigured)
        {
            return new ProQuotaPeriodResolution(
                fallback.Start,
                fallback.End,
                ResetAnchorSource.UserConfigured,
                true,
                false);
        }

        // No quota-cycle anchor is known. Show a rolling historical window rather
        // than silently dropping Sunday usage at an invented Monday quota reset.
        // Existing range helpers are half-open. Shift both bounds by one tick
        // to represent (now - 7 days, now] and include observations at the as-of instant.
        var historicalEnd = now.AddTicks(1);
        return new ProQuotaPeriodResolution(
            historicalEnd.AddDays(-7),
            historicalEnd,
            ResetAnchorSource.Default,
            false,
            true);
    }

    public static string WeeklyAllowanceId(AppSettings settings) =>
        settings.PlanPreset == SubscriptionPreset.Pro200 ? Pro200Gpt6Weekly : Pro100SharedWeekly;

    private readonly record struct WeeklyCandidate(DateTimeOffset ResetAt, ResetAnchorSource Source, DateTimeOffset? ObservedAt);

    private static WeeklyCandidate? SelectWeeklyReset(
        AppSettings settings,
        DateTimeOffset now,
        ProServerStatus? proStatus,
        DateTimeOffset? weeklyReset,
        QuotaMetadataSet? metadata)
    {
        var allowance = WeeklyAllowanceId(settings);
        var candidates = new List<WeeklyCandidate>();
        var window = metadata?.WeeklyWindow(settings.PlanPreset);
        if (window?.ResetAt is DateTimeOffset fromWindow && IsWeeklyKind(window.WindowKind, settings))
        {
            candidates.Add(new WeeklyCandidate(fromWindow, ResetAnchorSource.Server, window.ObservedAt));
        }

        if (weeklyReset is DateTimeOffset explicitWeekly)
        {
            candidates.Add(new WeeklyCandidate(explicitWeekly, ResetAnchorSource.Server, metadata?.ProServerStatus?.ObservedAt));
        }

        if (proStatus is { ResetConfidence: ServerResetConfidence.Server, ResetAt: { } live }
            && IsWeeklyServerReset(settings, proStatus, live))
        {
            candidates.Add(new WeeklyCandidate(live, ResetAnchorSource.Server, proStatus.ObservedAt));
        }

        var fresh = candidates
            .OrderByDescending(c => c.ObservedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(c => c.ResetAt)
            .FirstOrDefault();
        if (fresh.ResetAt != default)
        {
            return fresh;
        }

        if (proStatus?.LastConfirmedResetAt is DateTimeOffset retained
            && RetainedAnchorApplies(settings, proStatus, allowance))
        {
            return new WeeklyCandidate(retained, ResetAnchorSource.RetainedServer, proStatus.ObservedAt);
        }

        return null;
    }

    private static bool IsWeeklyKind(QuotaWindowKind kind, AppSettings settings) =>
        kind == QuotaWindowKind.Unclassified
        || kind == QuotaWindowKind.SharedProWeekly
        || (settings.PlanPreset == SubscriptionPreset.Pro200 && kind == QuotaWindowKind.Gpt6ProWeekly)
        || (settings.PlanPreset != SubscriptionPreset.Pro200 && kind == QuotaWindowKind.SharedProWeekly);

    private static bool IsWeeklyServerReset(AppSettings settings, ProServerStatus status, DateTimeOffset resetAt)
    {
        if (status.HasAmbiguousResets)
        {
            return false;
        }

        var matching = status.ModelLimits
            .Where(limit => limit.ResetAt is DateTimeOffset at && Math.Abs((at - resetAt).TotalSeconds) <= 2)
            .ToList();
        if (settings.PlanPreset == SubscriptionPreset.Pro200
            && matching.Count > 0
            && matching.All(limit => LooksLikeSolProSlug(limit.Slug)))
        {
            return false;
        }

        if (settings.PlanPreset == SubscriptionPreset.Pro200
            && matching.Count > 0
            && matching.All(limit => LooksLikeDailyWindow(limit)))
        {
            return false;
        }

        return true;
    }

    private static bool RetainedAnchorApplies(AppSettings settings, ProServerStatus status, string allowance)
    {
        if (string.IsNullOrWhiteSpace(status.LastConfirmedResetAllowanceId))
        {
            return settings.PlanPreset != SubscriptionPreset.Pro200;
        }

        return string.Equals(status.LastConfirmedResetAllowanceId, allowance, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSolProSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        var value = slug.ToLowerInvariant();
        return value.Contains("5-6-pro", StringComparison.Ordinal)
               || value.Contains("5.6-pro", StringComparison.Ordinal)
               || value.Contains("sol-pro", StringComparison.Ordinal);
    }

    private static bool LooksLikeDailyWindow(ProModelLimit limit) =>
        (limit.Description ?? "").Contains("daily", StringComparison.OrdinalIgnoreCase);
}
