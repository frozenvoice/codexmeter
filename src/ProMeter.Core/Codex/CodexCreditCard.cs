using System.Globalization;
using ProMeter.Services;

namespace ProMeter.Codex;

public sealed record CodexCreditExpiryRow(string Text, string Tooltip);

public sealed record CodexCreditCard(string CountText, IReadOnlyList<CodexCreditExpiryRow> Rows, string? Notice)
{
    public static CodexCreditCard From(CodexQuotaSnapshot snapshot, DateTimeOffset now)
    {
        if (snapshot.Status is CodexQuotaStatus.SignedOut or CodexQuotaStatus.CodexNotFound
            || snapshot.ResetCreditsAvailable is not { } count)
            return new("—", [], UiText.T("Credit details unavailable", "리셋권 정보 미제공"));

        var countText = count.ToString(CultureInfo.InvariantCulture) + UiText.T("", "개");
        if (count == 0) return new(countText, [], UiText.T("No reset credits available", "사용 가능한 리셋권이 없습니다"));
        var dates = snapshot.ResetCreditExpirations?.Where(x => x.HasValue).Select(x => x!.Value).OrderBy(x => x).ToList() ?? [];
        var rows = dates.GroupBy(x => x.ToLocalTime().Date).Select(group =>
        {
            var date = CodexDeadlineFormatting.DateStamp(group.First(), now);
            var text = UiText.T($"{date} · {group.Count()} " + (group.Count() == 1 ? "expires" : "expire"), $"{date} 만료 · {group.Count()}개");
            var tooltip = string.Join(Environment.NewLine, group.GroupBy(x => x).Select(exact =>
                $"{CodexDeadlineFormatting.DateStamp(exact.Key, now)} {exact.Key.ToLocalTime():HH:mm} · {exact.Count()}" + UiText.T(" credits", "개")));
            return new CodexCreditExpiryRow(text, tooltip);
        }).ToList();
        var notices = new List<string>();
        if (dates.Count == 0) notices.Add(UiText.T("Expiry not provided", "만료일 미제공"));
        else if (dates.Count != count) notices.Add(UiText.T("Some expiries unavailable", "일부 만료일 미제공"));
        if (dates.Any(x => x <= now)) notices.Add(UiText.T("An expiry has passed. Refresh to check availability.", "만료 시각이 지난 항목이 있습니다. 새로고침해 확인하세요."));
        if (snapshot.Status == CodexQuotaStatus.Stale) notices.Add(UiText.CodexDataStale);
        return new(countText, rows, notices.Count == 0 ? null : string.Join(Environment.NewLine, notices));
    }
}
