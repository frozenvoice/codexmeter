using CodexMeter.Services;

namespace CodexMeter.Codex;

public static class CodexMeterPresentation
{
    public static string StatusLabel(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.Available => UiText.T("Updated", "업데이트됨"),
        CodexQuotaStatus.Refreshing => UiText.T("Refreshing", "새로고침 중"),
        CodexQuotaStatus.Stale => UiText.T("Saved data", "이전 데이터"),
        CodexQuotaStatus.SignedOut => UiText.T("Sign in required", "로그인 필요"),
        CodexQuotaStatus.CodexNotFound => UiText.T("Codex not found", "Codex 찾을 수 없음"),
        _ => UiText.T("Refresh failed", "조회 실패")
    };

    public static string CompactText(CodexQuotaSnapshot snapshot, TaskbarStripMode mode = TaskbarStripMode.Full)
    {
        var ring = CodexRingPresentation.From(snapshot);
        var prefix = mode == TaskbarStripMode.Full ? "Codex " : "C ";
        if (!ring.IsAvailable) return prefix + "?";
        var suffix = snapshot.Status == CodexQuotaStatus.Stale ? " ~" : snapshot.Status == CodexQuotaStatus.Refreshing ? " …" : "";
        return prefix + CodexDisplayFormatting.PercentText(ring.UsedPercent) + suffix;
    }

    public static string Tooltip(CodexQuotaSnapshot snapshot)
    {
        var ring = CodexRingPresentation.From(snapshot);
        var usage = ring.IsAvailable
            ? CodexDisplayFormatting.CompactWindowKindLabel(snapshot.CompactWindow) + " " + ring.CenterValueText
            : CodexDisplayFormatting.StatusText(snapshot);
        return UiText.ProductName + Environment.NewLine + usage + Environment.NewLine + StatusLabel(snapshot);
    }
}
