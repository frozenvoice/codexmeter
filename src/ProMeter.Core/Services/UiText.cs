namespace ProMeter.Services;

public static class UiText
{
    public static UiLanguage Language { get; private set; } = UiLanguage.English;

    public static event Action? Changed;

    public static bool IsKorean => Language == UiLanguage.Korean;

    public static void SetLanguage(UiLanguage language)
    {
        if (Language == language)
        {
            return;
        }

        Language = language;
        Changed?.Invoke();
    }

    public static string T(string english, string korean) => IsKorean ? korean : english;

    public static string ProductName => "ProMeter";
    public static string GptPro => "GPT Pro";
    public static string Gpt6Pro => "GPT-6 Pro";
    public static string SolPro => "Sol Pro";

    public static string SyncNow => T("Sync now", "지금 동기화");
    public static string Syncing => T("Syncing", "동기화 중");
    public static string SyncingEllipsis => T("Syncing...", "동기화 중...");
    public static string LastSync => T("Last sync", "마지막 동기화");
    public static string Never => T("Never", "없음");
    public static string Settings => T("Settings", "설정");
    public static string OpenLogs => T("Open logs", "로그 폴더 열기");
    public static string StartWithWindows => T("Start with Windows", "Windows 시작 시 실행");
    public static string AutomaticSync => T("Automatic history synchronization", "자동 동기화");
    public static string Coverage => T("Coverage", "데이터 상태");
    public static string DataStatus => T("Data status", "데이터 상태");
    public static string Remaining => T("Remaining", "남은 횟수");
    public static string Reset => T("Reset", "리셋");
    public static string ResetTime => T("Reset time", "리셋 시각");
    public static string Estimate => T("Estimate", "추정 기준");
    public static string NotConfirmed => T("Not confirmed", "확인되지 않음");
    public static string NotAvailable => T("Not available", "확인되지 않음");
    public static string Limit => T("Limit", "한도");
    public static string LimitInfo => T("Limit", "한도 정보");
    public static string Today => T("Today", "오늘");
    public static string ThisWeek => T("This week", "이번 주");
    public static string Medium => T("Medium", "보통");
    public static string High => T("High", "높음");
    public static string ExtraHigh => T("Extra High", "매우 높음");
    public static string Complete => T("Complete", "완료");
    public static string Partial => T("Partial", "일부 누락");
    public static string Estimated => T("Estimated", "추정");
    public static string Unavailable => T("Unavailable", "확인 불가");
    public static string Failed => T("Failed", "실패");
    public static string PreviousData => T("Previous data", "이전 데이터");
    public static string Close => T("Close", "닫기");
    public static string Save => T("Save", "저장");
    public static string About => T("About", "정보");
    public static string Exit => T("Exit", "종료");
    public static string OpenProMeter => T("Open ProMeter", "ProMeter 열기");
    public static string OpenLogin => T("Open Login", "로그인 열기");
    public static string ViewStatistics => T("View statistics", "통계 보기");
    public static string Finish => T("Finish", "마침");
    public static string LanguageCaption => T("Language / 언어", "언어 / Language");
    public static string Korean => "한국어";
    public static string English => "English";

    public static string Idle => T("Idle", "대기");
    public static string SignedOut => T("Signed out", "로그아웃됨");
    public static string AuthenticationRequired => T("Authentication required", "인증 필요");
    public static string DetectingAccount => T("Detecting account...", "계정 확인 중...");
    public static string LoadingCatalog => T("Loading model catalog...", "모델 목록 불러오는 중...");
    public static string UpToDate => T("Up to date", "최신 상태");
    public static string RateLimited => T("Rate limited", "요청이 제한됨");
    public static string ApiChanged => T("API changed", "API가 변경됨");
    public static string ProviderSchemaMismatch => T("Provider schema mismatch", "응답 형식이 맞지 않음");
    public static string PartialData => T("Partial data", "일부 데이터 누락");
    public static string Offline => T("Offline", "오프라인");
    public static string Error => T("Error", "오류");
    public static string ScanningPeriod => T("Scanning current quota period...", "현재 한도 기간을 읽는 중...");
    public static string ScanningConversations => T("Scanning conversations...", "대화를 읽는 중...");

    public static string ChatGptSignedOut => T("ChatGPT tab is signed out.", "ChatGPT 탭이 로그아웃되어 있습니다.");
    public static string ChatGptSessionExpired => T("ChatGPT session expired.", "ChatGPT 세션이 만료되었습니다.");
    public static string ChatGptUnreachable => T("ChatGPT is unreachable.", "ChatGPT에 연결할 수 없습니다.");
    public static string RateLimitedPaused => T("Rate limited. Automatic sync paused.", "요청이 제한되어 자동 동기화를 잠시 멈췄습니다.");
    public static string AutoSyncPaused => T("Automatic sync paused after repeated failures.", "오류가 반복되어 자동 동기화를 잠시 멈췄습니다.");
    public static string SchemaMismatchStatus => T("Provider schema mismatch", "응답 형식이 맞지 않음");

    public static string CompanionConnected => T("Connected", "연결됨");
    public static string CompanionDisconnected => T("Disconnected", "연결 끊김");
    public static string CompanionRegisteredWaiting => T("Registered. Waiting for extension", "등록됨. 확장 프로그램 연결을 기다리는 중");
    public static string CompanionWaiting => T("Waiting for extension", "확장 프로그램 연결을 기다리는 중");
    public static string CompanionNotInstalled => T("Not installed", "설치되지 않음");
    public static string Forbidden403 => T(
        CompanionDiagnostics.Forbidden403,
        "ChatGPT가 페이지 요청을 거부했습니다 (403)");
    public static string NoChatGptTab => T(
        CompanionDiagnostics.NoChatGptTab,
        "ChatGPT를 열거나 로그인한 다음 다시 시도하세요");
    public static string PageBridgeUnavailable => T(
        CompanionDiagnostics.PageBridgeUnavailable,
        "ChatGPT 페이지 브리지를 사용할 수 없습니다");

    public static string IncompleteReconstruction => T("Incomplete reconstruction", "대화 기록 재구성이 불완전합니다");
    public static string ReconstructedHigh => T("Reconstructed · high confidence", "대화 기록 기반 · 높은 신뢰도");
    public static string ReconstructedEstimated => T("Reconstructed · estimated", "대화 기록 기반 · 추정");
    public static string ServerCount(int reconstructed) => T(
        $"Server count · reconstructed {reconstructed}",
        $"서버 집계 · 재구성 {reconstructed}");

    public static string CountBasisServer => T("Server", "서버");
    public static string CountBasisReconstructed => T("Reconstructed", "대화 기록 기반");
    public static string CountConfidenceAuthoritative => T("Authoritative", "공식");
    public static string CountConfidenceHigh => T("High", "높음");
    public static string CountConfidenceEstimated => T("Estimated", "추정");
    public static string CountConfidenceIncomplete => T("Incomplete", "불완전");
    public static string ResetBasisServer => T("Server", "서버");
    public static string ResetBasisUser => T("User configured", "사용자 설정");
    public static string ResetBasisEstimated => T("Estimated", "추정");
    public static string BranchIncluded => T("Included", "포함됨");
    public static string BranchUnknown => T("Unknown", "확인되지 않음");
    public static string CannotReconstruct => T("Cannot reconstruct", "재구성할 수 없음");
    public static string NormalChats => T("Normal chats", "일반 대화");
    public static string ArchivedChats => T("Archived chats", "보관된 대화");
    public static string Projects => T("Projects", "프로젝트");
    public static string ConversationBodies => T("Conversation bodies", "대화 본문");
    public static string TemporaryChats => T("Temporary chats", "임시 대화");
    public static string DeletedChats => T("Deleted chats", "삭제된 대화");
    public static string CountBasis => T("Count basis", "횟수 기준");
    public static string CountConfidence => T("Count confidence", "횟수 신뢰도");
    public static string ResetBasis => T("Reset basis", "리셋 기준");
    public static string BranchCoverage => T("Branch coverage", "분기 포함");
    public static string Successful => T("successful", "성공");
    public static string UniqueFailed => T("unique failed", "고유 실패");
    public static string CoverageDisclaimer => T(
        "History reconstruction is not an official OpenAI quota counter.",
        "대화 기록 재구성은 OpenAI의 공식 한도 집계가 아닙니다.");
    public static string TemporaryDeletedNote => T(
        "Temporary and deleted chats cannot be reconstructed from account history.",
        "임시 대화와 삭제된 대화는 계정 기록으로 재구성할 수 없습니다.");
    public static string HistoryLoadedWithoutUsage => T(
        SyncEngine.MissingAssistantUsageDiagnostic,
        "대화 기록은 불러왔지만 어시스턴트 사용량 메타데이터를 재구성하지 못했습니다.");
    public static string ReasoningReconstructedNote => T(
        "Sol reasoning counts are reconstructed statistics, not an official remaining quota.",
        "Sol 추론 횟수는 대화 기록에서 재구성한 통계이며, 공식 잔여 한도가 아닙니다.");

    public static string ResetServer(string stamp) => T($"{stamp} (server)", $"{stamp} (서버)");
    public static string ResetUserConfigured(string stamp) => T($"{stamp} (user configured)", $"{stamp} (사용자 설정)");

    public static string Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => T("Sunday", "일요일"),
        DayOfWeek.Monday => T("Monday", "월요일"),
        DayOfWeek.Tuesday => T("Tuesday", "화요일"),
        DayOfWeek.Wednesday => T("Wednesday", "수요일"),
        DayOfWeek.Thursday => T("Thursday", "목요일"),
        DayOfWeek.Friday => T("Friday", "금요일"),
        _ => T("Saturday", "토요일")
    };

    public static string ThemeSystem => T("System", "시스템");
    public static string ThemeLight => T("Light", "밝게");
    public static string ThemeDark => T("Dark", "어둡게");
    public static string TrayRemainingNumber => T("Remaining number", "남은 횟수 숫자");
    public static string TrayProgressRing => T("Progress ring", "진행 링");
    public static string TransportCompanion => T("Browser companion", "브라우저 도우미");
    public static string TransportWebView => T("WebView2 fallback", "WebView2 대체 경로");
    public static string TransportExport => T("Data Export only", "데이터 내보내기만");
    public static string FloatingWidget => T("Floating widget", "플로팅 위젯");
    public static string Plan => T("PLAN", "요금제");
    public static string ResetAnchor => T("RESET ANCHOR", "리셋 기준");
    public static string Connection => T("CONNECTION", "연결");
    public static string AppSection => T("APP", "앱");
    public static string Notifications => T("NOTIFICATIONS", "알림");
    public static string Data => T("DATA", "데이터");
    public static string Overview => T("Overview", "개요");
    public static string Trends => T("Trends", "추이");
    public static string Breakdown => T("Breakdown", "상세");
    public static string Status => T("STATUS", "상태");
    public static string SolReasoning => "SOL REASONING";
    public static string GptProSection => "GPT PRO";

    public static string WeeklyProQuota => T("Weekly Pro quota", "주간 Pro 한도");
    public static string DailyProQuota => T("Daily Pro quota", "일일 Pro 한도");
    public static string SolProDailyQuota => T("Sol Pro daily quota", "Sol Pro 일일 한도");
    public static string CombinedDailyQuota => T("Combined daily quota", "합산 일일 한도");
    public static string ReasoningQuota => T("Reasoning quota", "추론 한도");
    public static string SyncInterval => T("Sync interval (minutes)", "동기화 간격(분)");
    public static string CloseFlyoutOnDeactivate => T("Close flyout when it loses focus", "포커스를 잃으면 플라이아웃 닫기");
    public static string Notify20 => T("20% remaining", "20% 남음");
    public static string Notify10 => T("10% remaining", "10% 남음");
    public static string NotifyExhausted => T("Quota exhausted", "한도 소진");
    public static string NotifyReset => T("Quota reset", "한도 리셋");
    public static string NotifySyncError => T("Sync / authentication error", "동기화 / 인증 오류");
    public static string ImportOlder => T("Import older than the current quota period", "현재 한도 기간보다 이전 기록도 가져오기");
    public static string ImportConversations => T("Import conversations.json", "conversations.json 가져오기");
    public static string ExportJson => T("Export JSON", "JSON 내보내기");
    public static string ExportCsv => T("Export CSV", "CSV 내보내기");
    public static string RegisterNativeHost => T("Register Chrome/Edge native host", "Chrome/Edge 네이티브 호스트 등록");
    public static string TestWebView2 => T("Test WebView2", "WebView2 연결 테스트");
    public static string TestingWebView2 => T("Testing WebView2 connection...", "WebView2 연결 확인 중...");
    public static string CheckingChatGptSession => T("Checking ChatGPT session...", "ChatGPT 세션을 확인하는 중...");
    public static string WebViewInitializationTimedOut => T(
        "WebView2 initialization timed out.",
        "WebView2 초기화 시간이 초과되었습니다.");
    public static string WebViewInitializationFailed => T(
        "WebView2 initialization failed.",
        "WebView2 초기화에 실패했습니다.");
    public static string TechnicalDetail(string detail) => T($"Technical detail: {detail}", $"기술 세부 정보: {detail}");
    public static string WebViewDiagnosticTechnical(string operation, int status = 0, string? reason = null)
    {
        var operationLabel = operation switch
        {
            "session" => T("session", "세션"),
            "session-verification" => T("session verification", "세션 검증"),
            "account-detection" => T("account detection", "계정 확인"),
            "models" => T("models", "모델 목록"),
            "conversation-index" => T("conversation index", "대화 목록"),
            "interactive-login" => T("interactive sign-in", "대화형 로그인"),
            _ => T("diagnostic", "진단")
        };
        var statusLabel = status > 0 ? $"HTTP {status}" : T("unavailable", "사용할 수 없음");
        var reasonLabel = reason switch
        {
            "schema-mismatch" => T("schema mismatch", "응답 형식 불일치"),
            "incomplete-metadata" => T("incomplete metadata", "메타데이터 불완전"),
            "unsupported" => T("unsupported", "지원되지 않음"),
            "busy" => T("another sync or diagnostic is running", "다른 동기화 또는 진단이 실행 중"),
            "unavailable" => null,
            _ when !string.IsNullOrWhiteSpace(reason) => T("unavailable", "사용할 수 없음"),
            _ => null
        };
        return reasonLabel is null
            ? $"{operationLabel}: {statusLabel}"
            : $"{operationLabel}: {statusLabel}; {reasonLabel}";
    }
    public static string WebViewBaselineTechnical(bool sourceAvailable) => sourceAvailable
        ? T(
            "Browser Companion baseline: incomplete coverage or unconfirmed quota boundary",
            "Browser Companion 기준값: 데이터가 불완전하거나 한도 기간이 확인되지 않음")
        : T(
            "Browser Companion baseline: unavailable",
            "Browser Companion 기준값: 사용할 수 없음");
    public static string WebViewVerificationStatusTechnical(AppSyncStatus status) => T(
        $"WebView2 verification: {DisplayFormatting.StatusLabel(status)}",
        $"WebView2 검증: {DisplayFormatting.StatusLabel(status)}");
    public static string WebViewVerificationCountsDifferTechnical => T(
        "Reconstructed counts differ",
        "재구성 사용량이 다름");
    public static string WebViewVerificationUnavailableTechnical => T(
        "WebView2 verification: unavailable",
        "WebView2 검증: 사용할 수 없음");
    public static string WebViewVerificationBusyTechnical => T(
        "WebView2 verification: another sync or diagnostic is running",
        "WebView2 검증: 다른 동기화 또는 진단이 실행 중");
    public static string WebViewDiagnosticPass => T(
        "PASS\nWebView2 works on this PC/account.\nBrowser Companion can be removed after a full WebView2 verification sync succeeds.",
        "PASS\nWebView2가 이 PC와 계정에서 정상 동작합니다.\n전체 검증 동기화가 성공하면 Browser Companion 없이 사용할 수 있습니다.");
    public static string WebViewDiagnosticFailAuth => T(
        "FAIL_AUTH\nWebView2 sign-in is not supported for this account/login method.",
        "FAIL_AUTH\n이 계정의 로그인 방식은 WebView2에서 지원되지 않거나 로그인을 완료할 수 없습니다.");
    public static string WebViewDiagnosticFailSession => T(
        "FAIL_SESSION\nSign-in completed but the ChatGPT session could not be verified.",
        "FAIL_SESSION\n로그인은 완료됐지만 ChatGPT 세션을 확인하지 못했습니다.");
    public static string WebViewDiagnosticFailApi => T(
        "FAIL_API\nWebView2 session works, but ChatGPT history endpoints are unavailable.",
        "FAIL_API\nWebView2 세션은 정상이나 ChatGPT 기록 API를 사용할 수 없습니다.");
    public static string WebViewDiagnosticForbidden => T(
        "FORBIDDEN\nChatGPT rejected the WebView2 request. (HTTP 403)",
        "FORBIDDEN\nChatGPT가 WebView2 요청을 거부했습니다. (HTTP 403)");
    public static string WebViewDiagnosticCancelled => T(
        "CANCELLED\nTest cancelled. Current connection settings were not changed.",
        "CANCELLED\n테스트가 취소되었습니다. 현재 연결 설정은 변경되지 않았습니다.");
    public static string WebViewDiagnosticMessage(WebViewDiagnosticStatus status) => status switch
    {
        WebViewDiagnosticStatus.Pass => WebViewDiagnosticPass,
        WebViewDiagnosticStatus.FailAuth => WebViewDiagnosticFailAuth,
        WebViewDiagnosticStatus.FailSession => WebViewDiagnosticFailSession,
        WebViewDiagnosticStatus.Forbidden => WebViewDiagnosticForbidden,
        WebViewDiagnosticStatus.Cancelled => WebViewDiagnosticCancelled,
        _ => WebViewDiagnosticFailApi
    };
    public static string RunFullWebViewVerification => T(
        "Run full WebView2 verification sync",
        "WebView2 전체 검증 동기화");
    public static string RunningFullWebViewVerification => T(
        "Running full WebView2 verification sync...",
        "WebView2 전체 검증 동기화 중...");
    public static string BrowserCompanionCount(int count) => T(
        $"Browser Companion count: {count}",
        $"Browser Companion 사용량: {count}");
    public static string WebViewCount(int count) => T(
        $"WebView2 count: {count}",
        $"WebView2 사용량: {count}");
    public static string VerificationDifference(int difference) => T(
        $"Difference: {difference}",
        $"차이: {difference}");
    public static string BrowserBaselineReconstructed(bool estimated) => estimated
        ? T(
            "Browser Companion baseline: reconstructed / estimated.",
            "Browser Companion 기준값: 대화 기록 기반 / 추정.")
        : T(
            "Browser Companion baseline: reconstructed.",
            "Browser Companion 기준값: 대화 기록 기반.");
    public static string WebViewVerificationPassed => T(
        "Verification passed. Counts agree and both reconstructions are complete for the same quota period.",
        "검증을 통과했습니다. 같은 한도 기간의 두 재구성이 완전하며 사용량이 일치합니다.");
    public static string WebViewVerificationCountsDiffer => T(
        "Verification did not pass because the reconstructed counts differ.",
        "재구성 사용량이 달라 검증을 통과하지 못했습니다.");
    public static string WebViewVerificationIncompleteBaseline => T(
        "A complete Browser Companion baseline with a confirmed quota-period boundary is required.",
        "확인된 한도 기간을 사용하는 완전한 Browser Companion 기준값이 필요합니다.");
    public static string WebViewVerificationIncompleteWebView => T(
        "The WebView2 reconstruction was incomplete, so the comparison did not pass.",
        "WebView2 재구성이 불완전하여 비교를 통과하지 못했습니다.");
    public static string WebViewVerificationFailed => T(
        "The full WebView2 verification failed. Production usage data was not changed.",
        "WebView2 전체 검증에 실패했습니다. 운영 사용량 데이터는 변경되지 않았습니다.");
    public static string WebViewVerificationCancelled => T(
        "Verification cancelled. Production usage data was not changed.",
        "검증이 취소되었습니다. 운영 사용량 데이터는 변경되지 않았습니다.");
    public static string WebViewVerificationMessage(WebViewVerificationStatus status) => status switch
    {
        WebViewVerificationStatus.Passed => WebViewVerificationPassed,
        WebViewVerificationStatus.CountsDiffer => WebViewVerificationCountsDiffer,
        WebViewVerificationStatus.IncompleteBaseline => WebViewVerificationIncompleteBaseline,
        WebViewVerificationStatus.IncompleteWebView => WebViewVerificationIncompleteWebView,
        WebViewVerificationStatus.Cancelled => WebViewVerificationCancelled,
        _ => WebViewVerificationFailed
    };
    public static string UseWebViewAsDefault => T(
        "Use WebView2 as default connection",
        "WebView2를 기본 연결로 사용");
    public static string UseWebViewAsDefaultConfirmation => T(
        "Use WebView2 as the default connection? Browser Companion will remain installed and unchanged.",
        "WebView2를 기본 연결로 사용하시겠습니까? Browser Companion은 설치된 상태로 유지되며 변경되지 않습니다.");
    public static string UseWebViewAsDefaultSucceeded => T(
        "WebView2 is now the default connection. Browser Companion was not removed or changed.",
        "WebView2가 기본 연결로 설정되었습니다. Browser Companion은 제거되거나 변경되지 않았습니다.");
    public static string UseWebViewAsDefaultFailed => T(
        "WebView2 was not selected. A passing full verification and explicit confirmation are required.",
        "WebView2가 선택되지 않았습니다. 전체 검증 통과와 명시적 확인이 필요합니다.");
    public static string ResetAnchorHint => T(
        "Weekday and time are estimates unless you confirm them below. Saving other settings does not confirm this anchor.",
        "요일과 시각은 아래에서 확인하기 전까지 추정값입니다. 다른 설정을 저장해도 이 기준이 확정되지는 않습니다.");
    public static string ResetAnchorCheck => T(
        "Use this reset anchor when the server reset time is unavailable",
        "서버 리셋 시각을 모를 때 이 기준을 사용");
    public static string ConnectionHint => T(
        "Browser companion uses your normal Chrome or Edge ChatGPT session and avoids embedded social OAuth. It does not make unofficial ChatGPT endpoints official. WebView2 is a fallback only when that sign-in method works. Google/Microsoft/Apple login inside WebView2 is unsupported. Data Export remains the lower-risk non-real-time fallback.",
        "브라우저 도우미는 Chrome 또는 Edge의 일반 ChatGPT 세션을 사용하며, 내장 소셜 로그인을 피합니다. 비공식 ChatGPT 엔드포인트가 공식 API가 되는 것은 아닙니다. WebView2는 그 방식으로 로그인이 될 때만 대체 경로입니다. WebView2 안의 Google/Microsoft/Apple 로그인은 지원하지 않습니다. 데이터 내보내기는 실시간은 아니지만 위험이 더 낮은 대체 수단입니다.");
    public static string ChromeExtensionId => T("Chrome unpacked extension ID (32 letters a-p)", "Chrome 압축 해제 확장 ID (a-p 32글자)");
    public static string EdgeExtensionId => T("Edge unpacked extension ID if different", "다를 경우 Edge 압축 해제 확장 ID");
    public static string PairingTokenHint => T(
        "Local pairing token (not a ChatGPT secret). The native host attaches it automatically.",
        "로컬 페어링 토큰입니다. ChatGPT 비밀값이 아니며, 네이티브 호스트가 자동으로 붙입니다.");
    public static string AppRiskHint => T(
        "ProMeter uses unofficial ChatGPT web endpoints. They are not part of the public OpenAI API and may change. Programmatic history access is unsupported and may carry account or terms risk. Review current ChatGPT terms before enabling synchronization. Official ChatGPT Data Export import is the lower-risk fallback but is not real-time.",
        "ProMeter는 비공식 ChatGPT 웹 엔드포인트를 사용합니다. 공개 OpenAI API가 아니며 예고 없이 바뀔 수 있습니다. 프로그램으로 대화 기록에 접근하는 방식은 지원되지 않으며 계정 또는 이용약관 위험이 있을 수 있습니다. 동기화를 켜기 전에 현재 ChatGPT 약관을 확인하세요. 공식 ChatGPT 데이터 내보내기 가져오기는 위험이 더 낮지만 실시간이 아닙니다.");

    public static string WelcomeTitle => T("Welcome to ProMeter", "ProMeter에 오신 것을 환영합니다");
    public static string WelcomeSubtitle => T(
        "Windows tray monitor that reconstructs ChatGPT Pro usage from account conversation history.",
        "계정 대화 기록으로 ChatGPT Pro 사용량을 재구성하는 Windows 트레이 모니터입니다.");
    public static string WelcomeStep1 => T("1. Review before enabling sync", "1. 동기화를 켜기 전에 확인");
    public static string WelcomeStep1Body => T(
        "ProMeter uses unofficial ChatGPT web endpoints. These are not part of the public OpenAI API and may change without notice. Programmatic history access is unsupported and may conflict with applicable ChatGPT terms. Review current terms before enabling synchronization. Official ChatGPT Data Export import is the lower-risk fallback but is not real-time. Only a matching server quota counter is authoritative; reconstructed history counts are estimates. The browser companion avoids embedded OAuth; it does not make this integration official.",
        "ProMeter는 비공식 ChatGPT 웹 엔드포인트를 사용합니다. 공개 OpenAI API가 아니며 예고 없이 바뀔 수 있습니다. 프로그램으로 대화 기록에 접근하는 방식은 지원되지 않으며 적용되는 ChatGPT 약관과 충돌할 수 있습니다. 동기화를 켜기 전에 현재 약관을 확인하세요. 공식 ChatGPT 데이터 내보내기 가져오기는 위험이 더 낮지만 실시간이 아닙니다. 서버 한도 집계와 일치할 때만 공식으로 볼 수 있으며, 대화 기록으로 재구성한 횟수는 추정입니다. 브라우저 도우미는 내장 OAuth를 피하지만, 이 연동이 공식 기능이 되는 것은 아닙니다.");
    public static string WelcomeStep2 => T("2. Choose how to connect", "2. 연결 방식 선택");
    public static string WelcomeTransportCompanion => T(
        "Browser companion (recommended for Chrome/Edge social login)",
        "브라우저 도우미 (Chrome/Edge 소셜 로그인에 권장)");
    public static string WelcomeTransportWebView => T(
        "WebView2 fallback (only if sign-in works there)",
        "WebView2 대체 경로 (그 방식으로 로그인이 될 때만)");
    public static string WelcomeTransportExport => T(
        "Data Export import only (not real-time)",
        "데이터 내보내기 가져오기만 (실시간 아님)");
    public static string WelcomeSocialHint => T(
        "Google, Microsoft, and Apple sign-in are not supported inside WebView2. Use the browser companion with your normal browser session. ProMeter never spoofs a user agent.",
        "Google, Microsoft, Apple 로그인은 WebView2 안에서 지원되지 않습니다. 일반 브라우저 세션과 브라우저 도우미를 사용하세요. ProMeter는 사용자 에이전트를 위장하지 않습니다.");
    public static string WelcomeCompanionHint => T(
        "Load the unpacked extension, enter its ID, register the native host, then Connect in the popup. Finish can be used after registration without running a sync.",
        "압축 해제한 확장 프로그램을 로드하고 ID를 입력한 뒤 네이티브 호스트를 등록한 다음, 팝업에서 연결하세요. 등록 후에는 동기화 없이 마침을 사용할 수 있습니다.");
    public static string OpenExtensionFolder => T("Open extension folder", "확장 프로그램 폴더 열기");
    public static string WelcomeChromeId => T("Chrome extension ID (32 letters a-p)", "Chrome 확장 ID (a-p 32글자)");
    public static string WelcomeEdgeId => T("Edge extension ID if different", "다를 경우 Edge 확장 ID");
    public static string RegisterSelectedHost => T("Register selected native host", "선택한 네이티브 호스트 등록");
    public static string OpenChatGpt => T("Open chatgpt.com in your browser", "브라우저에서 chatgpt.com 열기");
    public static string WelcomeStep3 => T("3. Choose your Pro plan", "3. Pro 요금제 선택");
    public static string Plan100 => T("Pro $100 (50 shared weekly)", "Pro $100 (주 50회 공유)");
    public static string Plan200 => T("Pro $200 (200 weekly + daily Sol Pro)", "Pro $200 (주 200회 + 일일 Sol Pro)");
    public static string PlanCustom => T("Custom (edit later in Settings)", "사용자 지정 (나중에 설정에서 수정)");
    public static string WelcomeStep4 => T("4. Optional background behavior", "4. 선택적 백그라운드 동작");
    public static string WelcomeOptInHint => T(
        "Both options stay off unless you check them.",
        "직접 선택하지 않으면 두 옵션 모두 꺼진 상태로 유지됩니다.");
    public static string WelcomeStep5 => T("5. Sign in, then run a first manual sync", "5. 로그인한 뒤 첫 수동 동기화");
    public static string WelcomeSignInHint => T(
        "Sign-in alone does not scan history. After you are signed in, use Run first manual sync.",
        "로그인만으로는 기록을 읽지 않습니다. 로그인한 뒤 첫 수동 동기화를 실행하세요.");
    public static string RunFirstManualSync => T("Run first manual sync", "첫 수동 동기화 실행");
    public static string SignInConnect => T("Sign in / connect", "로그인 / 연결");
    public static string SignInAgain => T("Sign in again", "다시 로그인");
    public static string CompanionConnectedReady => T(
        "Companion connected. Run first manual sync when you are ready, or Finish to configure later.",
        "도우미가 연결되었습니다. 준비되면 첫 수동 동기화를 실행하거나, 마침으로 나중에 설정할 수 있습니다.");
    public static string DataExportSelected => T(
        "Data Export selected. Import conversations.json from Settings. No ChatGPT scan will run.",
        "데이터 내보내기가 선택되었습니다. 설정에서 conversations.json을 가져오세요. ChatGPT 검색은 실행되지 않습니다.");
    public static string OpeningWebViewSignIn => T("Opening WebView2 sign-in...", "WebView2 로그인을 여는 중...");
    public static string SignInCancelled => T(
        "Sign-in was cancelled. Google/Microsoft/Apple WebView login is unsupported.",
        "로그인이 취소되었습니다. WebView의 Google/Microsoft/Apple 로그인은 지원되지 않습니다.");
    public static string SignedInRunSync => T(
        "Signed in. Use Run first manual sync to scan history.",
        "로그인되었습니다. 첫 수동 동기화로 기록을 읽으세요.");
    public static string RunningFirstSync => T("Running first manual sync...", "첫 수동 동기화를 실행하는 중...");
    public static string CompanionNotInstalledPrefix => T("Not installed: ", "설치되지 않음: ");

    public static string AboutSubtitle => T(
        "Windows tray monitor for ChatGPT Pro quota and model usage.",
        "ChatGPT Pro 한도와 모델 사용량을 보는 Windows 트레이 모니터입니다.");
    public static string GitHubRepository => T("GitHub repository", "GitHub 저장소");
    public static string VersionPrefix => T("Version ", "버전 ");
    public static string DataSourceStatus => T("Data source status: ", "데이터 원본 상태: ");
    public static string SettingsTitle => T("ProMeter Settings", "ProMeter 설정");

    public static string GptProUsage(string usage) => T($"GPT Pro usage: {usage}", $"GPT Pro 사용량: {usage}");
    public static string GptProUsageServer(string usage, int reconstructed) => T(
        $"GPT Pro usage: {usage}  (server · reconstructed {reconstructed})",
        $"GPT Pro 사용량: {usage}  (서버 · 재구성 {reconstructed})");
    public static string RemainingWithCount(string remaining) => T($" remaining {remaining}", $" 남은 횟수 {remaining}");
    public static string CurrentPeriod(string start, string end) => T($"Current period {start} – {end}", $"현재 기간 {start} – {end}");
    public static string Soon => T("soon", "곧");

    public static string OnboardingUpToDate(int used, int limit) =>
        T($"GPT Pro usage: {used} / {limit}", $"GPT Pro 사용량: {used} / {limit}");
    public static string OnboardingPartial(int used, int limit) => T(
        $"Partial usage {used} / {limit}. Coverage is incomplete. You can inspect Coverage or retry.",
        $"일부 사용량 {used} / {limit}. 데이터 상태가 불완전합니다. 데이터 상태를 확인하거나 다시 시도할 수 있습니다.");
    public static string OnboardingIncompleteCount(int limit) => T(
        $"GPT Pro: ? / {limit}. Incomplete reconstruction.",
        $"GPT Pro: ? / {limit}. 대화 기록 재구성이 불완전합니다.");
    public static string OnboardingAuthRequired => T(
        "Authentication required. Sign in again before a history scan.",
        "인증이 필요합니다. 기록을 읽기 전에 다시 로그인하세요.");
    public static string OnboardingRateLimited => T(
        "Rate limited. Wait and retry the first manual sync later.",
        "요청이 제한되었습니다. 잠시 후 첫 수동 동기화를 다시 시도하세요.");
    public static string OnboardingSchemaMismatch => T(
        "Provider schema mismatch. A zero count is not a successful load.",
        "응답 형식이 맞지 않습니다. 0회는 성공적인 불러오기가 아닙니다.");

    public static string ToastNewPeriod => T("A new GPT Pro quota period has started.", "새 GPT Pro 한도 기간이 시작되었습니다.");
    public static string ToastGptProExhausted => T("GPT Pro quota is exhausted.", "GPT Pro 한도가 소진되었습니다.");
    public static string ToastGptPro10 => T("10% of GPT Pro quota remaining.", "GPT Pro 한도가 10% 남았습니다.");
    public static string ToastGptPro20 => T("20% of GPT Pro quota remaining.", "GPT Pro 한도가 20% 남았습니다.");
    public static string ToastSolExhausted => T("GPT-5.6 Sol Pro daily quota is exhausted.", "GPT-5.6 Sol Pro 일일 한도가 소진되었습니다.");
    public static string ToastSol10 => T("10% of Sol Pro daily quota remaining.", "Sol Pro 일일 한도가 10% 남았습니다.");
    public static string ToastSol20 => T("20% of Sol Pro daily quota remaining.", "Sol Pro 일일 한도가 20% 남았습니다.");
    public static string ToastCombinedExhausted => T("Combined Pro daily quota is exhausted.", "합산 Pro 일일 한도가 소진되었습니다.");
    public static string ToastCombined10 => T("10% of combined Pro daily quota remaining.", "합산 Pro 일일 한도가 10% 남았습니다.");
    public static string ToastCombined20 => T("20% of combined Pro daily quota remaining.", "합산 Pro 일일 한도가 20% 남았습니다.");
    public static string ToastSyncTitle => T("ProMeter sync", "ProMeter 동기화");

    public static string ImportTitle => T("Import official ChatGPT conversations.json", "공식 ChatGPT conversations.json 가져오기");
    public static string ImportedEvents(int count) => T($"Imported {count} usage events.", $"{count}개의 사용 기록을 가져왔습니다.");
    public static string RegisteredNativeHosts => T(
        "Registered the official Chrome/Edge native hosts.\n\n",
        "공식 Chrome/Edge 네이티브 호스트를 등록했습니다.\n\n");
    public static string NativeHostFailed => T("native-host registration failed", "네이티브 호스트 등록에 실패했습니다");
    public static string TimeColumn => T("Time", "시각");
    public static string ModelColumn => T("Model", "모델");
    public static string RawColumn => T("Raw", "원본");
    public static string EffortColumn => T("Effort", "추론 강도");
    public static string FamilyColumn => T("Family", "계열");
    public static string SourceColumn => T("Source", "출처");
    public static string Gpt6ProWeek => T("GPT-6 Pro week", "GPT-6 Pro 주간");
    public static string SolProDaily => T("Sol Pro daily", "Sol Pro 일일");
    public static string CombinedDaily => T("Combined daily", "합산 일일");
    public static string Plan100Short => "Pro $100";
    public static string Plan200Short => "Pro $200";
    public static string PlanCustomShort => T("Custom", "사용자 지정");
}
