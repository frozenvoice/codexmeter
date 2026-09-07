using System.Globalization;
using ProMeter.Services;

namespace ProMeter.Codex;

public sealed record CodexDisplayRow(string Label, string Value, bool EmphasizeDanger, string? Detail = null, string? Tooltip = null);

public static class CodexDisplayFormatting
{
    private static string FormatStale(CodexQuotaSnapshot snapshot)
    {
        var recent = RecentFailureText(snapshot.TechnicalDetail);
        return string.IsNullOrWhiteSpace(recent)
            ? UiText.CodexDataStale
            : $"{UiText.CodexDataStale}{Environment.NewLine}{UiText.CodexRecentRefreshError}: {recent}";
    }

    public static string? RecentFailureText(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        if (detail.Contains("protocol", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("response-error", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("root-keys", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.CodexProtocolChanged;
        }

        if (detail.Contains("timed-out", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.CodexTimedOut;
        }

        if (detail.Contains("cancelled", StringComparison.OrdinalIgnoreCase))
        {
            return UiText.CodexCancelled;
        }

        return null;
    }

    public static string SectionTitle => "CODEX";

    public static string StatusText(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.Refreshing when !snapshot.HasUsablePercentages => UiText.CodexRefreshing,
        CodexQuotaStatus.Refreshing => UiText.CodexRefreshing,
        CodexQuotaStatus.Stale => FormatStale(snapshot),
        CodexQuotaStatus.CodexNotFound => UiText.CodexNotFound,
        CodexQuotaStatus.SignedOut => UiText.CodexSignIn,
        CodexQuotaStatus.ProtocolMismatch => UiText.CodexProtocolChanged,
        CodexQuotaStatus.TimedOut => UiText.CodexTimedOut,
        CodexQuotaStatus.Cancelled => UiText.CodexCancelled,
        CodexQuotaStatus.Unavailable => UiText.CodexUnavailable,
        CodexQuotaStatus.Available => "",
        _ => UiText.CodexUnavailable
    };

    public static IReadOnlyList<CodexDisplayRow> Rows(CodexQuotaSnapshot snapshot, DateTimeOffset? now = null)
    {
        if (snapshot.Status is CodexQuotaStatus.CodexNotFound or CodexQuotaStatus.SignedOut
            || (!snapshot.HasUsablePercentages
                && snapshot.Status is CodexQuotaStatus.Unavailable
                    or CodexQuotaStatus.ProtocolMismatch
                    or CodexQuotaStatus.TimedOut
                    or CodexQuotaStatus.Cancelled
                    or CodexQuotaStatus.Refreshing))
        {
            return [];
        }

        var at = now ?? DateTimeOffset.Now;
        var rows = new List<CodexDisplayRow>();
        foreach (var window in snapshot.Windows)
        {
            var usedLabel = UsedLabel(window);
            var remainingLabel = RemainingLabel(window);
            rows.Add(new CodexDisplayRow(usedLabel, PercentText(window.UsedPercent), window.UsedPercent >= 100));
            if (window.RemainingPercent is not null)
            {
                rows.Add(new CodexDisplayRow(remainingLabel, PercentText(window.RemainingPercent), false));
            }

            rows.Add(new CodexDisplayRow(UiText.Reset, ResetStamp(window.ResetsAt), false,
                CodexDeadlineFormatting.Remaining(window.ResetsAt, at)));
        }

        if (snapshot.ResetCreditsAvailable is int credits)
        {
            var expiry = CodexDeadlineFormatting.CreditExpiry(snapshot, at);
            rows.Add(new CodexDisplayRow(UiText.ResetCredits, credits.ToString(CultureInfo.InvariantCulture), false, expiry.Detail, expiry.Tooltip));
        }

        if (snapshot.LastSuccessfulRefresh is { } checkedAt)
        {
            rows.Add(new CodexDisplayRow(UiText.LastChecked, TimeOfDay(checkedAt), false));
        }

        return rows;
    }

    public static string OverviewText(CodexQuotaSnapshot snapshot)
    {
        var status = StatusText(snapshot);
        var rows = Rows(snapshot);
        if (rows.Count == 0)
        {
            return string.IsNullOrWhiteSpace(status) ? UiText.CodexUnavailable : status;
        }

        var body = string.Join("   ", rows.Select(row => $"{row.Label} {row.Value}"));
        return string.IsNullOrWhiteSpace(status) ? body : $"{status}   ·   {body}";
    }

    public static IReadOnlyList<string> DiagnosticLines(CodexQuotaSnapshot snapshot, bool executableFound)
    {
        return
        [
            $"{UiText.CodexExecutable}: {(executableFound ? UiText.Found : UiText.NotFound)}",
            $"{UiText.CodexSignInState}: {SignInLabel(snapshot)}",
            $"{UiText.CodexFreshness}: {FreshnessLabel(snapshot)}",
            $"{UiText.LastChecked}: {(snapshot.LastSuccessfulRefresh is { } success ? TimeOfDay(success) : UiText.Never)}",
            $"{UiText.CodexWindows}: {WindowSummary(snapshot)}",
            $"{UiText.CodexFailureCategory}: {snapshot.TechnicalDetail ?? snapshot.Status.ToString()}"
        ];
    }

    public static string DurationLabel(int? minutes)
    {
        if (minutes is null or <= 0)
        {
            return UiText.T("Unknown duration", "알 수 없는 기간");
        }

        if (minutes == CodexWindowClassifier.FiveHourMinutes)
        {
            return UiText.T("5-hour", "5시간");
        }

        if (minutes == CodexWindowClassifier.WeeklyMinutes)
        {
            return UiText.T("Weekly", "주간");
        }

        if (minutes % 1440 == 0)
        {
            var days = minutes.Value / 1440;
            return days == 1
                ? UiText.T("24-hour", "24시간")
                : UiText.T($"{days}-day", $"{days}일");
        }

        if (minutes % 60 == 0)
        {
            var hours = minutes.Value / 60;
            return UiText.T($"{hours}-hour", $"{hours}시간");
        }

        return UiText.T($"{minutes} min", $"{minutes}분");
    }

    public static string ResetStamp(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null)
        {
            return UiText.NotAvailable;
        }

        var local = resetsAt.Value.ToLocalTime();
        var now = DateTimeOffset.Now.ToLocalTime();
        if (local.Date == now.Date)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return DisplayFormatting.FormatStamp(local);
    }

    public static string TimeOfDay(DateTimeOffset value) =>
        value.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    public static string PercentText(double? value) =>
        value is { } percent
            ? $"{Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%"
            : "?";

    public static string CompactWindowKindLabel(CodexQuotaWindow? window)
    {
        if (window is null)
        {
            return UiText.CodexUsage;
        }

        return window.Kind switch
        {
            CodexWindowKind.Weekly => UiText.T("Codex weekly usage", "Codex 주간 사용"),
            CodexWindowKind.FiveHour => UiText.T("Codex 5-hour usage", "Codex 5시간 사용"),
            _ => UiText.T($"Codex {DurationLabel(window.WindowDurationMinutes)} usage", $"Codex {DurationLabel(window.WindowDurationMinutes)} 사용")
        };
    }

    private static string UsedLabel(CodexQuotaWindow window) => window.Kind switch
    {
        CodexWindowKind.FiveHour => UiText.FiveHourUsed,
        CodexWindowKind.Weekly => UiText.WeeklyUsed,
        _ => UiText.T($"{DurationLabel(window.WindowDurationMinutes)} used", $"{DurationLabel(window.WindowDurationMinutes)} 사용량")
    };

    private static string RemainingLabel(CodexQuotaWindow window) => window.Kind switch
    {
        CodexWindowKind.FiveHour => UiText.FiveHourRemaining,
        CodexWindowKind.Weekly => UiText.WeeklyRemaining,
        _ => UiText.T($"{DurationLabel(window.WindowDurationMinutes)} remaining", $"{DurationLabel(window.WindowDurationMinutes)} 남음")
    };

    private static string SignInLabel(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.SignedOut => UiText.SignedOut,
        CodexQuotaStatus.CodexNotFound => UiText.NotFound,
        CodexQuotaStatus.Available or CodexQuotaStatus.Stale or CodexQuotaStatus.Refreshing => UiText.T("Signed in", "로그인됨"),
        _ => UiText.Unavailable
    };

    private static string FreshnessLabel(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.Available => UiText.T("Fresh", "최신"),
        CodexQuotaStatus.Stale => UiText.T("Stale", "오래됨"),
        CodexQuotaStatus.Refreshing => UiText.CodexRefreshing,
        _ => UiText.Unavailable
    };

    private static string WindowSummary(CodexQuotaSnapshot snapshot)
    {
        if (snapshot.Windows.Count == 0)
        {
            return UiText.NotAvailable;
        }

        return string.Join(
            ", ",
            snapshot.Windows.Select(window =>
                $"{DurationLabel(window.WindowDurationMinutes)} {PercentText(window.UsedPercent)}"));
    }
}
