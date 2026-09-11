using System.Diagnostics;
using System.Windows.Input;
using CodexMeter.Codex;
using CodexMeter.Providers.Claude;
using CodexMeter.Providers.Usage;

namespace CodexMeter.UI;

public partial class ClaudeConnectionWindow : Window
{
    public ClaudeConnectionWindow(CodexAccountProfile profile, string executable)
    {
        if (profile.Provider != UsageProviderId.Claude) throw new ArgumentException("Wrong usage provider.");
        InitializeComponent();
        Title = UiText.ProductName + " · Claude";
        Heading.Text = UiText.T("Connect Claude Code", "Claude Code 연결");
        ProfileName.Text = new CodexAccountView(profile, CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable)).DisplayName;
        SetupSteps.Text = UiText.T("1. Copy the settings below and merge the statusLine entry into the Claude Code settings used for this account (for example ~/.claude/settings.json). Keep your other settings.\n2. Use Claude Code and complete a response. Its official statusLine sends usage to this CycleArc profile. No additional login is needed here.",
            "1. 아래 설정을 복사해 이 계정에서 사용하는 Claude Code 설정(예: ~/.claude/settings.json)에 statusLine 항목을 병합하세요. 다른 설정은 유지하세요.\n2. Claude Code를 사용해 응답을 받으세요. 공식 statusLine이 이 CycleArc 프로필에 사용량을 전송합니다. 여기서 별도로 로그인할 필요는 없습니다.");
        ConnectionJson.Text = ClaudeStatusLineCommand.SettingsJson(executable, profile.Id);
        System.Windows.Automation.AutomationProperties.SetName(ConnectionJson, "Claude Code statusLine settings");
        CopyButton.Content = UiText.T("Copy statusLine settings", "statusLine 설정 복사");
        ExistingStatusLineHint.Text = UiText.T("Already have a statusLine? Keep your existing command in a wrapper that passes the same stdin to CycleArc as well. The copied settings replace the statusLine entry if pasted directly. CycleArc does not edit Claude settings.",
            "기존 statusLine이 있다면 동일한 stdin을 CycleArc에도 전달하는 래퍼에서 기존 명령을 유지하세요. 복사한 설정을 바로 붙여 넣으면 statusLine 항목이 바뀝니다. CycleArc가 Claude 설정을 직접 수정하지는 않습니다.");
        IdentityHint.Text = UiText.T("The statusLine contains no account email or ID. This profile's name is local. Use a separate profile and command for each Claude account, and change the command when switching accounts. CycleArc cannot verify which account emitted the data.",
            "statusLine에는 계정 이메일·ID가 없습니다. 이 프로필 이름은 로컬 이름입니다. Claude 계정마다 프로필과 명령을 따로 사용하고, 계정을 바꾸면 명령도 바꾸세요. CycleArc는 어느 계정이 보낸 데이터인지 확인할 수 없습니다.");
        FreshnessHint.Text = UiText.T("Only the official 5-hour and 7-day usage percentages and reset times are saved. Missing fields stay unknown. Without a valid update for 5 minutes, or after a reported reset passes, the last values are marked stale. Refresh reads received data; it does not ask Claude for new usage. Rate-limit fields can be absent before the first response or for unsupported plans.",
            "공식 5시간·7일 사용률과 리셋 시각만 저장합니다. 없는 정보는 알 수 없음으로 남깁니다. 5분 동안 정상 데이터가 없거나 리셋 시각이 지나면 마지막 값을 오래됨으로 표시합니다. 새로고침은 받은 데이터를 읽으며 Claude에 새 사용량을 요청하지 않습니다. 첫 응답 전이거나 지원하지 않는 플랜에서는 한도 정보가 없을 수 있습니다.");
        DocsButton.Content = UiText.T("Official statusLine documentation", "공식 statusLine 문서");
        DoneButton.Content = UiText.Close;
        SourceInitialized += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            MaxHeight = Math.Max(400, work.Height - 24); Height = Math.Min(Height, MaxHeight);
            MaxWidth = Math.Max(470, work.Width - 24); Width = Math.Min(Width, MaxWidth);
        };
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { System.Windows.Clipboard.SetText(ConnectionJson.Text); CopyStatus.Text = UiText.T("Copied. Merge into Claude Code settings.", "복사했습니다. Claude Code 설정에 병합하세요."); }
        catch { CopyStatus.Text = UiText.T("Could not copy. Select and copy the text above.", "복사하지 못했습니다. 위 내용을 선택해 복사하세요."); }
    }
    private void OnDocs(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://code.claude.com/docs/en/statusline") { UseShellExecute = true });
    private void OnDone(object sender, RoutedEventArgs e) => Close();
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
}
