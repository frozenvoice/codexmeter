using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public static class ClaudeUsagePresentation
{
    public const string UsagePageUrl = "https://claude.ai/settings/usage";
    public static string Title => UiText.T("Claude subscription usage", "Claude 구독 사용량");
    public static string SharedScope => UiText.T("Shared across Web, Desktop and Code", "Web·Desktop·Code 공유 한도");
    public static string UsagePageLabel => UiText.T("Open usage page", "사용량 페이지 열기");
    public static string UsagePageHint => UiText.T("Check current limits in your browser under the intended Claude account. Opening the page does not update CycleArc.",
        "브라우저에서 확인할 Claude 계정으로 로그인한 뒤 현재 한도를 보세요. 페이지를 열어도 CycleArc 수치는 갱신되지 않습니다.");

    public static string StatusText(CodexQuotaSnapshot snapshot)
    {
        if (snapshot.Status == CodexQuotaStatus.Available)
            return UiText.T("Last values received via Claude Code. Account usage may have changed since then.",
                "Claude Code를 통해 마지막으로 받은 값입니다. 이후 계정 사용량은 달라졌을 수 있습니다.");
        if (snapshot.TechnicalDetail == "claude-connected-waiting")
            return UiText.T("Connected; no subscription usage received yet. Samples arrive via Claude Code responses. Check the usage page for current limits.",
                "연결됐지만 구독 사용량은 아직 수신하지 못했습니다. Claude Code 응답을 통해 값을 받습니다. 현재 한도는 사용량 페이지에서 확인하세요.");
        if (snapshot.Status == CodexQuotaStatus.SignedOut)
            return UiText.T("Claude is disconnected. Open Connect to reconnect this profile.", "Claude 연결이 해제되었습니다. 연결 버튼에서 다시 연결할 수 있습니다.");
        if (snapshot.TechnicalDetail == "claude-statusline-malformed" || snapshot.Status == CodexQuotaStatus.ProtocolMismatch)
            return snapshot.HasUsablePercentages
                ? UiText.T("Claude statusLine data could not be read. Showing the last valid values · stale.",
                    "Claude statusLine 데이터를 읽지 못했습니다. 마지막 정상값 표시 · 오래됨.")
                : UiText.T("Unsupported Claude statusLine data. Check your Claude Code version and connection command.",
                    "Claude statusLine 형식을 확인할 수 없습니다. Claude Code 버전과 연결 명령을 확인하세요.");
        if (snapshot.Status == CodexQuotaStatus.Stale)
            return UiText.T("Last subscription values received via Claude Code · stale. Check the usage page for current limits.",
                "Claude Code를 통해 마지막으로 받은 구독 사용량 · 오래됨. 현재 한도는 사용량 페이지에서 확인하세요.");
        return UiText.T("No Claude subscription usage received. Connect this profile to receive samples via Claude Code. Limits may be absent before the first response or on unsupported plans.",
            "Claude 구독 사용량을 아직 받지 못했습니다. 프로필을 연결하면 Claude Code를 통해 값을 받습니다. 첫 응답 전이거나 지원하지 않는 플랜이면 한도 정보가 없을 수 있습니다.");
    }
}
