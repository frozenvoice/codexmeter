namespace ProMeter.Models;

public sealed class QuotaWindow
{
    public bool Found { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
    public int? Used { get; set; }
    public int? Limit { get; set; }
    public string? FeatureName { get; set; }

    public bool IsAuthoritative => Used is not null && Limit is not null && ResetAt is not null;
}

public sealed class QuotaMetadataSet
{
    public QuotaWindow? SharedProWeekly { get; set; }
    public QuotaWindow? Gpt6ProWeekly { get; set; }
    public QuotaWindow? SolProDaily { get; set; }
    public QuotaWindow? CombinedProDaily { get; set; }
    public List<QuotaWindow> Diagnostics { get; } = [];
    public string? RawSummary { get; set; }

    public bool Found =>
        SharedProWeekly is not null
        || Gpt6ProWeekly is not null
        || SolProDaily is not null
        || CombinedProDaily is not null
        || Diagnostics.Count > 0;

    public bool MatchesGptProAllowance =>
        SharedProWeekly is not null
        || Gpt6ProWeekly is not null
        || SolProDaily is not null
        || CombinedProDaily is not null;

    public DateTimeOffset? WeeklyResetAt => SharedProWeekly?.ResetAt ?? Gpt6ProWeekly?.ResetAt;

    public QuotaWindow? WeeklyWindow(SubscriptionPreset plan) =>
        plan == SubscriptionPreset.Pro200 ? Gpt6ProWeekly : SharedProWeekly;
}
