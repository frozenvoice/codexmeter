namespace CycleArc.Services;

public sealed class QuotaEngine
{
    public QuotaSnapshot Build(
        IReadOnlyList<UsageEvent> events,
        AppSettings settings,
        DateTimeOffset now,
        DateTimeOffset? lastSync,
        CoverageInfo coverage,
        QuotaMetadataSet? serverQuota,
        AppSyncStatus status,
        string? statusDetail = null)
    {
        var weekly = serverQuota?.WeeklyWindow(settings.PlanPreset);
        var proStatus = serverQuota?.ProServerStatus ?? ProServerStatus.Unknown();
        var period = ProQuotaPeriodResolver.Resolve(settings, now, proStatus, weekly?.ResetAt, serverQuota);
        var start = period.Start;
        var end = period.End;
        var resetSource = period.Source;
        var todayStart = QuotaPeriodCalculator.LocalDayStart(now, settings);
        var weekStart = QuotaPeriodCalculator.LocalWeekStart(now, settings);
        var periodEvents = new List<UsageEvent>();
        var unresolved = 0;
        var heuristic = 0;
        var legacyPending = 0;
        foreach (var usage in events)
        {
            if (IsLegacyUnverified(usage))
            {
                legacyPending++;
                unresolved++;
                continue;
            }

            if (IsPeriodAmbiguous(usage, start, end))
            {
                unresolved++;
                continue;
            }

            if (!usage.Countable || usage.UnresolvedKind != UnresolvedEvidenceKind.None || !usage.HasUsableTimestamp)
            {
                if (usage.QuotaFamily == QuotaFamily.GptPro || usage.UnresolvedKind != UnresolvedEvidenceKind.None)
                {
                    unresolved++;
                }

                continue;
            }

            if (usage.DedupeConfidence == DedupeConfidence.Heuristic && usage.QuotaFamily == QuotaFamily.GptPro)
            {
                heuristic++;
            }

            if (QuotaPeriodCalculator.InRange(usage.CreatedAt, start, end))
            {
                periodEvents.Add(usage);
            }
        }

        var todayEvents = events.Where(e => CountableInRange(e, todayStart, todayStart.AddDays(1), start, end)).ToList();
        var weekEvents = events.Where(e => CountableInRange(e, weekStart, weekStart.AddDays(7), start, end)).ToList();
        var proEvents = periodEvents.Where(e => e.QuotaFamily == QuotaFamily.GptPro).ToList();
        var reasoningWeek = weekEvents.Where(e => e.QuotaFamily == QuotaFamily.SolReasoning).ToList();
        var todayReasoning = todayEvents.Where(e => e.QuotaFamily == QuotaFamily.SolReasoning).ToList();
        var reconstructed = proEvents.Count;
        var gpt6Weekly = proEvents.Count(IsGpt6Pro);
        var todaySol = todayEvents.Count(IsSolPro);
        var combinedToday = todayEvents.Count(e => e.QuotaFamily == QuotaFamily.GptPro);
        var useServerWeekly = weekly is not null && weekly.IsAuthoritativeAt(now);
        var allowSolDaily = AllowsDailyWindow(settings, settings.SolProDailyQuota);
        var allowCombinedDaily = AllowsDailyWindow(settings, settings.CombinedDailyQuota);
        var solWindow = allowSolDaily ? serverQuota?.SolProDaily : null;
        var combinedWindow = allowCombinedDaily ? serverQuota?.CombinedProDaily : null;
        var useServerSol = solWindow is not null && solWindow.IsAuthoritativeAt(now);
        var useServerCombined = combinedWindow is not null && combinedWindow.IsAuthoritativeAt(now);
        var historyComplete = coverage is { NormalChats: true, IndexIncomplete: false, ConversationIncomplete: false, FailedConversations: 0 };
        var periodTrusted = period.CurrentCycleKnown;
        var lowerBound = unresolved == 0
                         && heuristic == 0
                         && reconstructed >= 0
                         && (coverage.IndexIncomplete || coverage.ConversationIncomplete || coverage.FailedConversations > 0);

        coverage.ResetAnchorSource = resetSource;
        coverage.ResetTimeAuthoritative = resetSource == ResetAnchorSource.Server && !period.NextResetEstimated;
        coverage.QuotaMetadataAuthoritative = useServerWeekly;
        coverage.CountConfidence = useServerWeekly
            ? CoverageConfidence.Authoritative
            : historyComplete && periodTrusted && unresolved == 0
                ? CoverageConfidence.HighConfidence
                : CoverageConfidence.Estimated;
        coverage.ResetConfidence = resetSource switch
        {
            ResetAnchorSource.Server when !period.NextResetEstimated => CoverageConfidence.Authoritative,
            ResetAnchorSource.Server => CoverageConfidence.HighConfidence,
            ResetAnchorSource.RetainedServer => CoverageConfidence.HighConfidence,
            ResetAnchorSource.UserConfigured => CoverageConfidence.HighConfidence,
            _ => CoverageConfidence.Estimated
        };
        coverage.TemporaryChats = false;
        coverage.DeletedChats = false;

        var reconstructedTop = settings.PlanPreset == SubscriptionPreset.Pro200 ? gpt6Weekly : reconstructed;
        var used = useServerWeekly ? weekly!.Used!.Value : reconstructedTop;
        var limit = useServerWeekly ? weekly!.Limit!.Value : Math.Max(1, settings.WeeklyProQuota);
        var solLimit = solWindow?.Limit ?? (allowSolDaily ? settings.SolProDailyQuota : null);
        var combinedLimit = combinedWindow?.Limit ?? (allowCombinedDaily ? settings.CombinedDailyQuota : null);
        var solUsed = useServerSol ? solWindow!.Used!.Value : todaySol;
        var combinedUsed = useServerCombined ? combinedWindow!.Used!.Value : combinedToday;
        var usageUnavailable = !useServerWeekly
            && reconstructed == 0
            && coverage.HistoryLoadedWithoutUsage
            && (coverage.Confidence == CoverageConfidence.Incomplete || status == AppSyncStatus.ProviderSchemaMismatch);

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
            ResetAt = period.CurrentCycleKnown ? end : null,
            ResetEstimated = resetSource != ResetAnchorSource.Server || period.NextResetEstimated,
            ResetAnchorSource = resetSource,
            CurrentCycleKnown = period.CurrentCycleKnown,
            LastSync = lastSync,
            Status = status,
            StatusDetail = statusDetail,
            Coverage = coverage,
            TodayPro = todayEvents.Count(e => e.QuotaFamily == QuotaFamily.GptPro),
            TodaySolPro = solUsed,
            CombinedToday = combinedUsed,
            Gpt6WeeklyUsed = gpt6Weekly,
            ReconstructedUsed = reconstructed,
            UnresolvedCount = unresolved,
            LegacyPendingCount = legacyPending,
            HeuristicReconstructedCount = heuristic,
            ReconstructionIsLowerBound = lowerBound && !useServerWeekly,
            UsesServerWeeklyCount = useServerWeekly,
            UsesServerSolDailyCount = useServerSol,
            UsesServerCombinedDailyCount = useServerCombined,
            DisplayUsageUnavailable = usageUnavailable,
            ProServerStatus = proStatus,
            SolProDailyLimit = solLimit,
            CombinedDailyLimit = combinedLimit,
            Reasoning = new ReasoningStats
            {
                Today = todayReasoning.Count,
                ThisWeek = reasoningWeek.Count,
                Medium = reasoningWeek.Count(e => e.ReasoningEffort == ReasoningEffort.Medium),
                High = reasoningWeek.Count(e => e.ReasoningEffort == ReasoningEffort.High),
                ExtraHigh = reasoningWeek.Count(e => e.ReasoningEffort == ReasoningEffort.ExtraHigh),
                Unknown = reasoningWeek.Count(e => e.ReasoningEffort == ReasoningEffort.Unknown),
                Limit = settings.ReasoningQuota
            }
        };
    }

    public IReadOnlyList<DailyTrendPoint> BuildTrend(
        IReadOnlyList<UsageEvent> events,
        DateTimeOffset start,
        DateTimeOffset end,
        AppSettings? settings = null)
    {
        var days = new List<DailyTrendPoint>();
        var zoneId = settings?.ResetTimeZoneId;
        var startDate = QuotaPeriodCalculator.LocalDate(start, zoneId);
        var lastInstant = end.AddTicks(-1);
        var endDate = QuotaPeriodCalculator.LocalDate(lastInstant < start ? start : lastInstant, zoneId);
        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var slice = events
                .Where(e => !IsLegacyUnverified(e)
                            && e.HasUsableTimestamp
                            && QuotaPeriodCalculator.LocalDate(e.CreatedAt, zoneId) == date)
                .ToList();
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

    /// <summary>
    /// Rows derived before the current reconstruction semantics keep their evidence but must not
    /// contribute to any displayed reconstruction until they are actually revalidated.
    /// </summary>
    public static bool IsLegacyUnverified(UsageEvent usage) =>
        usage.UnresolvedKind == UnresolvedEvidenceKind.LegacyUnverified
        || usage.TimestampProvenance == TimestampProvenance.LegacyUnverified
        || usage.ReconstructionVersion < ReconstructionSemantics.Version;

    private static bool CountableInRange(UsageEvent usage, DateTimeOffset start, DateTimeOffset end, DateTimeOffset periodStart, DateTimeOffset periodEnd)
    {
        if (IsLegacyUnverified(usage))
        {
            return false;
        }

        if (!usage.Countable || usage.UnresolvedKind != UnresolvedEvidenceKind.None || !usage.HasUsableTimestamp)
        {
            return false;
        }

        if (IsPeriodAmbiguous(usage, periodStart, periodEnd) || IsPeriodAmbiguous(usage, start, end))
        {
            return false;
        }

        return QuotaPeriodCalculator.InRange(usage.CreatedAt, start, end);
    }

    private static bool IsPeriodAmbiguous(UsageEvent usage, DateTimeOffset start, DateTimeOffset end)
    {
        if (usage.PeriodAmbiguous)
        {
            return true;
        }

        if (usage.RequestStartedAt is not DateTimeOffset begun || usage.ResponseCompletedAt is not DateTimeOffset finished)
        {
            return false;
        }

        return (begun < start && finished >= start) || (begun < end && finished >= end);
    }

    private static bool AllowsDailyWindow(AppSettings settings, int? configuredLimit) =>
        settings.PlanPreset switch
        {
            SubscriptionPreset.Pro200 => true,
            SubscriptionPreset.Custom => configuredLimit is not null,
            _ => false
        };

    private static bool ContainsAny(string? value, params string[] needles) =>
        !string.IsNullOrWhiteSpace(value)
        && needles.Any(needle => value.Contains(needle, StringComparison.OrdinalIgnoreCase));
}
