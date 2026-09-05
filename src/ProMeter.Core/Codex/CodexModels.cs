namespace ProMeter.Codex;

public enum CodexQuotaStatus
{
    Available,
    Refreshing,
    Stale,
    CodexNotFound,
    SignedOut,
    Unavailable,
    ProtocolMismatch,
    TimedOut,
    Cancelled
}

public enum CodexWindowKind
{
    FiveHour,
    Weekly,
    Other
}

public sealed record CodexQuotaWindow(
    string? LimitId,
    double? UsedPercent,
    int? WindowDurationMinutes,
    DateTimeOffset? ResetsAt,
    CodexWindowKind Kind)
{
    public double? RemainingPercent =>
        UsedPercent is { } used
            ? Math.Clamp(100 - used, 0, 100)
            : null;
}

public sealed record CodexQuotaSnapshot(
    CodexQuotaStatus Status,
    string? PlanType,
    DateTimeOffset? LastSuccessfulRefresh,
    DateTimeOffset? LastAttemptedRefresh,
    bool? OrdinaryUsageAllowed,
    string? RateLimitReachedType,
    int? ResetCreditsAvailable,
    IReadOnlyList<CodexQuotaWindow> Windows,
    string? TechnicalDetail)
{
    public static CodexQuotaSnapshot Empty(CodexQuotaStatus status, string? detail = null) =>
        new(status, null, null, null, null, null, null, [], detail);

    public bool HasUsablePercentages => Windows.Any(window => window.UsedPercent is not null);

    public CodexQuotaWindow? CompactWindow =>
        Windows.FirstOrDefault(window => window.Kind == CodexWindowKind.Weekly)
        ?? Windows.FirstOrDefault(window => window.Kind == CodexWindowKind.FiveHour)
        ?? Windows
            .Where(window => window.WindowDurationMinutes is > 0)
            .OrderByDescending(window => window.WindowDurationMinutes)
            .FirstOrDefault()
        ?? Windows.FirstOrDefault();

    public CodexQuotaSnapshot AsStale(DateTimeOffset attempted, string? detail)
    {
        if (!HasUsablePercentages)
        {
            return this with
            {
                Status = Status is CodexQuotaStatus.CodexNotFound or CodexQuotaStatus.SignedOut
                    ? Status
                    : CodexQuotaStatus.Unavailable,
                LastAttemptedRefresh = attempted,
                TechnicalDetail = detail
            };
        }

        return this with
        {
            Status = CodexQuotaStatus.Stale,
            LastAttemptedRefresh = attempted,
            TechnicalDetail = detail
        };
    }

    public CodexQuotaSnapshot AsRefreshing() => this with { Status = CodexQuotaStatus.Refreshing };
}

public sealed record CodexRefreshResult(
    CodexQuotaSnapshot Snapshot,
    bool UsedCache,
    string? FailureCategory);

public static class CodexWindowClassifier
{
    public const int FiveHourMinutes = 300;
    public const int WeeklyMinutes = 10_080;

    public static CodexWindowKind FromDurationMinutes(int? minutes) => minutes switch
    {
        FiveHourMinutes => CodexWindowKind.FiveHour,
        WeeklyMinutes => CodexWindowKind.Weekly,
        > 0 => CodexWindowKind.Other,
        _ => CodexWindowKind.Other
    };
}
