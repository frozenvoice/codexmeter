namespace ProMeter.Services;

public sealed class QuotaEngine
{
    public QuotaSnapshot Build(
        IReadOnlyList<UsageEvent> events,
        AppSettings settings,
        DateTimeOffset now,
        DateTimeOffset? lastSync,
        CoverageInfo coverage,
        QuotaMetadata? serverQuota,
        AppSyncStatus status,
        string? statusDetail = null)
    {
        var serverReset = serverQuota is { IsAuthoritative: true, ResetAt: not null } ? serverQuota.ResetAt : null;
        var (start, end) = QuotaPeriodCalculator.CurrentPeriod(settings, now, serverReset);
        var todayStart = QuotaPeriodCalculator.LocalDayStart(now, settings);
        var periodEvents = events.Where(e => QuotaPeriodCalculator.InRange(e.CreatedAt, start, end)).ToList();
        var todayEvents = events.Where(e => e.CreatedAt >= todayStart).ToList();
        var proEvents = periodEvents.Where(e => e.QuotaFamily == QuotaFamily.GptPro).ToList();
        var reasoning = periodEvents.Where(e => e.QuotaFamily == QuotaFamily.SolReasoning).ToList();
        var todayReasoning = todayEvents.Where(e => e.QuotaFamily == QuotaFamily.SolReasoning).ToList();

        coverage.ResetTimeAuthoritative = serverReset is not null;
        coverage.QuotaMetadataAuthoritative = serverQuota?.IsAuthoritative == true;

        return new QuotaSnapshot
        {
            Used = proEvents.Count,
            Limit = Math.Max(1, settings.WeeklyProQuota),
            ModelBreakdown = proEvents
                .GroupBy(e => e.NormalizedModel)
                .Select(g => new ModelCount
                {
                    NormalizedModel = g.Key,
                    RawModel = g.Select(x => x.RawModel).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .ToList(),
            PeriodStart = start,
            PeriodEnd = end,
            ResetAt = end,
            ResetEstimated = serverReset is null,
            LastSync = lastSync,
            Status = status,
            StatusDetail = statusDetail,
            Coverage = coverage,
            TodayPro = todayEvents.Count(e => e.QuotaFamily == QuotaFamily.GptPro),
            TodaySolPro = todayEvents.Count(IsSolPro),
            CombinedToday = todayEvents.Count(e => e.QuotaFamily == QuotaFamily.GptPro),
            Reasoning = new ReasoningStats
            {
                Today = todayReasoning.Count,
                ThisWeek = reasoning.Count,
                Medium = reasoning.Count(e => e.ReasoningEffort == ReasoningEffort.Medium),
                High = reasoning.Count(e => e.ReasoningEffort == ReasoningEffort.High),
                ExtraHigh = reasoning.Count(e => e.ReasoningEffort == ReasoningEffort.ExtraHigh),
                Unknown = reasoning.Count(e => e.ReasoningEffort == ReasoningEffort.Unknown),
                Limit = settings.ReasoningQuota
            }
        };
    }

    public IReadOnlyList<DailyTrendPoint> BuildTrend(IReadOnlyList<UsageEvent> events, DateTimeOffset start, DateTimeOffset end)
    {
        var days = new List<DailyTrendPoint>();
        for (var date = DateOnly.FromDateTime(start.UtcDateTime); date <= DateOnly.FromDateTime(end.UtcDateTime.AddTicks(-1)); date = date.AddDays(1))
        {
            var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            var dayEnd = dayStart.AddDays(1);
            var slice = events.Where(e => e.CreatedAt >= dayStart && e.CreatedAt < dayEnd).ToList();
            days.Add(new DailyTrendPoint
            {
                Date = date,
                ProCount = slice.Count(e => e.QuotaFamily == QuotaFamily.GptPro),
                ReasoningCount = slice.Count(e => e.QuotaFamily == QuotaFamily.SolReasoning),
                InstantCount = slice.Count(e => e.QuotaFamily == QuotaFamily.Instant),
                OtherCount = slice.Count(e => e.QuotaFamily == QuotaFamily.Unknown)
            });
        }

        return days;
    }

    private static bool IsSolPro(UsageEvent e) =>
        e.QuotaFamily == QuotaFamily.GptPro
        && e.NormalizedModel.Contains("5.6", StringComparison.OrdinalIgnoreCase);
}
