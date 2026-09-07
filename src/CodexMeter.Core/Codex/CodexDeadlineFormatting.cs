using System.Globalization;
using CodexMeter.Services;

namespace CodexMeter.Codex;

public static class CodexDeadlineFormatting
{
    public static string? Remaining(DateTimeOffset? deadline, DateTimeOffset now)
    {
        if (deadline is null) return null;
        var left = deadline.Value - now;
        if (left <= TimeSpan.Zero) return UiText.T("Awaiting refresh", "갱신 대기");
        if (left.TotalDays >= 1)
            return left.Hours == 0 ? UiText.T($"{left.Days}d left", $"{left.Days}일 남음")
                : UiText.T($"{left.Days}d {left.Hours}h left", $"{left.Days}일 {left.Hours}시간 남음");
        if (left.TotalHours >= 1)
            return left.Minutes == 0 ? UiText.T($"{left.Hours}h left", $"{left.Hours}시간 남음")
                : UiText.T($"{left.Hours}h {left.Minutes}m left", $"{left.Hours}시간 {left.Minutes}분 남음");
        return left.TotalMinutes >= 1 ? UiText.T($"{left.Minutes}m left", $"{left.Minutes}분 남음")
            : UiText.T("Less than a minute", "1분 미만 남음");
    }

    public static string DateStamp(DateTimeOffset date, DateTimeOffset now)
    {
        var local = date.ToLocalTime();
        var sameYear = local.Year == now.ToLocalTime().Year;
        return UiText.IsKorean ? local.ToString(sameYear ? "M월 d일" : "yyyy년 M월 d일", CultureInfo.InvariantCulture)
            : local.ToString(sameYear ? "MMM d" : "MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    public static (string? Detail, string? Tooltip) CreditExpiry(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.ResetCreditsAvailable is not > 0) return (null, null);
        var dates = snapshot.ResetCreditExpirations;
        var known = dates?.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList() ?? [];
        if (known.Count == 0) return (UiText.T("Expiry not provided", "만료일 미제공"), null);
        var complete = known.Count == snapshot.ResetCreditsAvailable;
        var groups = known.GroupBy(x => x.ToLocalTime().Date).ToList();
        var detail = string.Join(Environment.NewLine, groups.Take(3).Select(group =>
            UiText.T($"{DateStamp(group.First(), now)} · {group.Count()} " + (group.Count() == 1 ? "expires" : "expire"), $"{DateStamp(group.First(), now)} 만료 · {group.Count()}개")));
        if (groups.Count > 3) detail += Environment.NewLine + UiText.T($"+{groups.Count - 3} more dates", $"외 {groups.Count - 3}개 날짜");
        if (!complete) detail += Environment.NewLine + UiText.T("Some expiries unavailable", "일부 만료일 미제공");
        var lines = known.GroupBy(x => x).Select(group =>
            $"{DateStamp(group.Key, now)} {group.Key.ToLocalTime():HH:mm} · {group.Count()}" + UiText.T(" credits", "개")).ToList();
        if (!complete) lines.Add(UiText.T("Some credit expiry details are unavailable.", "일부 리셋권의 만료 정보는 제공되지 않았습니다."));
        if (known.Any(x => x <= now)) lines.Add(UiText.T("An expiry time has passed. Refresh to check availability.", "만료 시각이 지난 항목이 있습니다. 새로고침해 확인하세요."));
        return (detail, string.Join(Environment.NewLine, lines));
    }
}
