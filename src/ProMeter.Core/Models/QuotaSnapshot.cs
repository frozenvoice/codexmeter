namespace ProMeter.Models;

public sealed class QuotaSnapshot
{
    public int Used { get; set; }
    public int Limit { get; set; }
    public int Remaining => Math.Max(0, Limit - Used);
    public double PercentUsed => Limit <= 0 ? 0 : Math.Clamp(Used / (double)Limit, 0, 1);
    public IReadOnlyList<ModelCount> ModelBreakdown { get; set; } = [];
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
    public bool ResetEstimated { get; set; } = true;
    public ResetAnchorSource ResetAnchorSource { get; set; } = ResetAnchorSource.Default;
    public DateTimeOffset? LastSync { get; set; }
    public AppSyncStatus Status { get; set; } = AppSyncStatus.Idle;
    public string? StatusDetail { get; set; }
    public CoverageInfo Coverage { get; set; } = new();
    public ReasoningStats Reasoning { get; set; } = new();
    public int TodayPro { get; set; }
    public int TodaySolPro { get; set; }
    public int CombinedToday { get; set; }
    public int Gpt6WeeklyUsed { get; set; }
    public int ReconstructedUsed { get; set; }
    public bool UsesServerCount { get; set; }
    public int? SolProDailyLimit { get; set; }
    public int? CombinedDailyLimit { get; set; }
    public int SolProDailyRemaining => SolProDailyLimit is int limit ? Math.Max(0, limit - TodaySolPro) : 0;
    public int CombinedDailyRemaining => CombinedDailyLimit is int limit ? Math.Max(0, limit - CombinedToday) : 0;
}

public sealed class ModelCount
{
    public string NormalizedModel { get; set; } = "";
    public string RawModel { get; set; } = "";
    public int Count { get; set; }
}

public sealed class ReasoningStats
{
    public int Today { get; set; }
    public int ThisWeek { get; set; }
    public int Medium { get; set; }
    public int High { get; set; }
    public int ExtraHigh { get; set; }
    public int Unknown { get; set; }
    public int? Limit { get; set; }
}

public sealed class DailyTrendPoint
{
    public DateOnly Date { get; set; }
    public int ProCount { get; set; }
    public int ReasoningCount { get; set; }
    public int InstantCount { get; set; }
    public int OtherCount { get; set; }
}

public sealed class SyncProgress
{
    public string Phase { get; set; } = "";
    public int IndexCount { get; set; }
    public int ChangedConversations { get; set; }
    public int ProcessedConversations { get; set; }
    public int ParsedEvents { get; set; }
    public string? Detail { get; set; }
}

public sealed class ProviderResponse
{
    public int Status { get; set; }
    public string Body { get; set; } = "";
    public string? RetryAfter { get; set; }
    public bool SchemaMismatch { get; set; }
    public string? Error { get; set; }

    public bool IsSuccess => Status is >= 200 and < 300;
    public bool IsUnauthorized => Status is 401 or 403;
    public bool IsRateLimited => Status == 429;
    public bool IsServerError => Status is >= 500 and < 600;
    public bool IsOffline => Status == 0 && !SchemaMismatch;
}
