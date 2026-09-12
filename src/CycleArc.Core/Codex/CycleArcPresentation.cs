using CycleArc.Services;
using CycleArc.Providers.Usage;

namespace CycleArc.Codex;

public static class CycleArcPresentation
{
    public static string StatusLabel(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.Unavailable when snapshot.TechnicalDetail == "claude-connected-waiting" => UiText.T("Awaiting usage", "수신 대기"),
        CodexQuotaStatus.Unavailable when snapshot.Provider == UsageProviderId.Claude => UiText.T("Waiting for data", "데이터 대기 중"),
        CodexQuotaStatus.ProtocolMismatch when snapshot.Provider == UsageProviderId.Claude => UiText.ProviderSchemaMismatch,
        CodexQuotaStatus.SignedOut when snapshot.Provider == UsageProviderId.Claude => UiText.T("Disconnected", "미연결"),
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
        var prefix = mode == TaskbarStripMode.Full ? snapshot.Provider.Name() + " "
            : snapshot.Provider == UsageProviderId.Claude ? "Cl " : "C ";
        if (!ring.IsAvailable) return prefix + "?";
        var suffix = snapshot.Status == CodexQuotaStatus.Stale ? " ~" : snapshot.Status == CodexQuotaStatus.Refreshing ? " …" : "";
        return prefix + CodexDisplayFormatting.PercentText(ring.UsedPercent, snapshot.Provider) + suffix;
    }

    public static string Tooltip(CodexQuotaSnapshot snapshot)
    {
        var ring = CodexRingPresentation.From(snapshot);
        var usage = ring.IsAvailable
            ? CodexDisplayFormatting.CompactWindowKindLabel(snapshot.CompactWindow, snapshot.Provider) + " " + ring.CenterValueText
            : CodexDisplayFormatting.StatusText(snapshot);
        return UiText.ProductName + " · " + snapshot.Provider.Name() + Environment.NewLine
            + usage + Environment.NewLine + StatusLabel(snapshot);
    }
}
