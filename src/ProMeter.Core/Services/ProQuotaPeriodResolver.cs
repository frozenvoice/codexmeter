namespace ProMeter.Services;

public readonly record struct ProQuotaPeriodResolution(
    DateTimeOffset Start,
    DateTimeOffset End,
    ResetAnchorSource Source,
    bool CurrentCycleKnown,
    bool NextResetEstimated);

public static class ProQuotaPeriodResolver
{
    public static ProQuotaPeriodResolution Resolve(
        AppSettings settings,
        DateTimeOffset now,
        ProServerStatus? proStatus,
        DateTimeOffset? weeklyReset = null)
    {
        if (proStatus is { ResetConfidence: ServerResetConfidence.Server, ResetAt: { } live })
        {
            var (start, end) = QuotaPeriodCalculator.PeriodContaining(now, live);
            return new ProQuotaPeriodResolution(start, end, ResetAnchorSource.Server, true, now >= live);
        }

        if (proStatus?.LastConfirmedResetAt is DateTimeOffset retained)
        {
            var (start, end) = QuotaPeriodCalculator.PeriodContaining(now, retained);
            return new ProQuotaPeriodResolution(start, end, ResetAnchorSource.RetainedServer, true, true);
        }

        if (weeklyReset is DateTimeOffset weekly)
        {
            var (start, end) = QuotaPeriodCalculator.PeriodContaining(now, weekly);
            return new ProQuotaPeriodResolution(start, end, ResetAnchorSource.Server, true, now >= weekly);
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

        return new ProQuotaPeriodResolution(
            fallback.Start,
            fallback.End,
            ResetAnchorSource.Default,
            false,
            true);
    }
}
