using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public static class ClaudeUsagePresentation
{
    public static string StatusText(CodexQuotaSnapshot snapshot)
    {
        if (snapshot.Status == CodexQuotaStatus.Available) return "";
        if (snapshot.TechnicalDetail == "claude-connected-waiting")
            return UiText.T("Connected. Waiting for a Claude Code terminal response to receive usage. Chats on claude.ai or in the desktop app do not update CycleArc.",
                "계정은 연결됐습니다. Claude Code 터미널에서 응답을 받으면 사용량을 수신합니다. claude.ai 웹·데스크톱 채팅만으로는 갱신되지 않습니다.");
        if (snapshot.Status == CodexQuotaStatus.SignedOut)
            return UiText.T("Claude is disconnected. Open Connect to reconnect this profile.", "Claude 연결이 해제되었습니다. 연결 버튼에서 다시 연결할 수 있습니다.");
        if (snapshot.TechnicalDetail == "claude-statusline-malformed" || snapshot.Status == CodexQuotaStatus.ProtocolMismatch)
            return snapshot.HasUsablePercentages
                ? UiText.T("Claude statusLine data could not be read. Showing the last valid values · stale.",
                    "Claude statusLine 데이터를 읽지 못했습니다. 마지막 정상값 표시 · 오래됨.")
                : UiText.T("Unsupported Claude statusLine data. Check your Claude Code version and connection command.",
                    "Claude statusLine 형식을 확인할 수 없습니다. Claude Code 버전과 연결 명령을 확인하세요.");
        if (snapshot.Status == CodexQuotaStatus.Stale)
            return UiText.T("No current Claude Code data. Showing the last received values · stale. Use Claude Code to receive an update.",
                "최신 Claude Code 데이터가 없습니다. 마지막 수신값 표시 · 오래됨. Claude Code를 사용하면 업데이트를 받을 수 있습니다.");
        return UiText.T("Waiting for Claude Code statusLine data. Connect this profile, then use Claude Code. Rate limits may be absent before the first response or for an unsupported plan.",
            "Claude Code statusLine 데이터를 기다리고 있습니다. 이 프로필을 연결한 뒤 Claude Code를 사용하세요. 첫 응답 전이거나 지원하지 않는 플랜이면 한도 정보가 없을 수 있습니다.");
    }
}
