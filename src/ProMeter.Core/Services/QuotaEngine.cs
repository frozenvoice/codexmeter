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
        var matched = serverQuota is { MatchesGptProAllowance: true };
        var serverReset = matched && serverQuota?.ResetAt is not null ? serverQuota.ResetAt : null;
        var (start, end) = QuotaPeriodCalculator.CurrentPeriod(settings, now, serverReset);
        var todayStart = QuotaPeriodCalculator.LocalDayStart(now, settings);
        var periodEvents = events.Where(e => QuotaPeriodCalculator.InRange(e.CreatedAt, start, end)).ToList();
        var todayEvents = events.Where(e => e.CreatedAt >= todayStart).ToList();
        var proEvents = periodEvents.Where(e => e.QuotaFamily == QuotaFamily.GptPro).ToList();
        var reasoning = periodEvents.Where(e => e.QuotaFamily == QuotaFamily.SolReasoning).ToList();
        var todayReasoning = todayEvents.Where(e => e.QuotaFamily == QuotaFamily.SolReasoning).ToList();
        var reconstructed = proEvents.Count;
        var gpt6Weekly = proEvents.Count(IsGpt6Pro);
        var todaySol = todayEvents.Count(IsSolPro);
        var combinedToday = todayEvents.Count(e => e.QuotaFamily == QuotaFamily.GptPro);
        var useServerCount = matched
            && serverQuota?.Used is not null
            && serverQuota.Limit is not null
            && serverQuota.ResetAt is not null;

        coverage.ResetTimeAuthoritative = serverReset is not null;
        coverage.QuotaMetadataAuthoritative = useServerCount;
        coverage.CountConfidence = useServerCount
            ? CoverageConfidence.Authoritative
            : coverage is { NormalChats: true, IndexIncomplete: false, ConversationIncomplete: false, FailedConversations: 0 }
                ? CoverageConfidence.HighConfidence
                : CoverageConfidence.Estimated;
        coverage.ResetConfidence = serverReset is not null
            ? CoverageConfidence.Authoritative
            : CoverageConfidence.Estimated;
        coverage.TemporaryChats = false;
        coverage.DeletedChats = false;

        var used = useServerCount ? serverQuota!.Used!.Value : settings.PlanPreset == SubscriptionPreset.Pro200 ? gpt6Weekly : reconstructed;
        var limit = useServerCount ? serverQuota!.Limit!.Value : Math.Max(1, settings.WeeklyProQuota);

        return new QuotaSnapshot
        {
            Used = used,
            Limit = limit,
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
            ResetEstimated = coverage.ResetConfidence != CoverageConfidence.Authoritative,
            LastSync = lastSync,
            Status = status,
            StatusDetail = statusDetail,
            Coverage = coverage,
            TodayPro = todayEvents.Count(e => e.QuotaFamily == QuotaFamily.GptPro),
            TodaySolPro = todaySol,
            CombinedToday = combinedToday,
            Gpt6WeeklyUsed = gpt6Weekly,
            ReconstructedUsed = reconstructed,
            UsesServerCount = useServerCount,
            SolProDailyLimit = settings.SolProDailyQuota,
            CombinedDailyLimit = settings.CombinedDailyQuota,
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

    public static bool IsSolPro(UsageEvent e)
    {
        if (e.QuotaFamily != QuotaFamily.GptPro)
        {
            return false;
        }

        return ContainsAny(e.NormalizedModel, "5.6", "5-6", "sol pro")
               || ContainsAny(e.RawModel, "5-6-pro", "5.6-pro", "sol-pro");
    }

    public static bool IsGpt6Pro(UsageEvent e)
    {
        if (e.QuotaFamily != QuotaFamily.GptPro || IsSolPro(e))
        {
            return false;
        }

        var slug = ModelNormalizer.NormalizeSlug(e.RawModel);
        return ContainsAny(e.NormalizedModel, "gpt-6", "gpt 6")
               || slug.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string? value, params string[] needles) =>
        !string.IsNullOrWhiteSpace(value)
        && needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
