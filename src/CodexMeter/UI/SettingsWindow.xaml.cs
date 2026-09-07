using System.Windows.Automation;
using System.Windows.Input;

namespace CodexMeter.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public event Action<AppSettings>? Saved;
    public event Action? OpenLogsRequested;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Title = UiText.ProductName + " · " + UiText.Settings;
        WindowHeading.Text = Title;
        GeneralTab.Header = UiText.T("General", "일반");
        WidgetTab.Header = UiText.T("Widget", "위젯");
        ConnectionTab.Header = UiText.T("Connection", "연결");
        AppearanceTitle.Text = UiText.T("Make it yours", "표시와 동작");
        AppearanceHint.Text = UiText.T("Choose how CodexMeter looks and starts.", "화면과 시작 방식을 설정하세요.");
        ThemeLabel.Text = UiText.T("Theme", "테마");
        ThemeBox.ItemsSource = new[] { UiText.T("System", "시스템"), UiText.T("Light", "밝게"), UiText.T("Dark", "어둡게") };
        ThemeBox.SelectedIndex = (int)settings.Theme;
        LanguageLabel.Text = UiText.T("Language", "언어");
        LanguageBox.ItemsSource = new[] { "한국어", "English" };
        LanguageBox.SelectedIndex = settings.UiLanguage == UiLanguage.Korean ? 0 : 1;
        IconLabel.Text = UiText.T("Tray icon", "트레이 아이콘");
        IconBox.ItemsSource = new[] { UiText.T("Usage number", "사용률 숫자"), UiText.T("Usage ring", "사용률 링") };
        IconBox.SelectedIndex = (int)settings.TrayIconStyle;
        StartupLabel.Text = UiText.StartWithWindows;
        StartupBox.IsChecked = settings.StartWithWindows;
        TrayHint.Text = UiText.T("Usage appears in the Windows notification area. Click the icon for details.",
            "Windows 알림 영역에 사용률을 표시합니다. 아이콘을 누르면 상세 카드가 열립니다.");
        WidgetTitle.Text = UiText.T("Desktop widget", "바탕화면 위젯");
        WidgetHint.Text = UiText.T("Keep a small usage display on your desktop. Drag it to move.", "작은 사용률 표시를 바탕화면에 둡니다. 드래그해서 위치를 옮길 수 있습니다.");
        WidgetLabel.Text = UiText.T("Show widget", "위젯 표시");
        WidgetBox.IsChecked = settings.FloatingWidgetEnabled;
        WidgetOpacityLabel.Text = UiText.T("Opacity", "불투명도");
        WidgetOpacityBox.Value = settings.WidgetOpacity;
        UpdateOpacityText();
        WidgetTopLabel.Text = UiText.T("Always on top", "항상 위에 표시");
        WidgetTopBox.IsChecked = settings.WidgetAlwaysOnTop;
        WidgetClickThroughLabel.Text = UiText.T("Click through", "클릭 통과");
        WidgetClickThroughBox.IsChecked = settings.WidgetClickThrough;
        WidgetClickThroughBox.ToolTip = UiText.T("Mouse clicks pass to the window behind the widget.", "마우스 클릭이 위젯 뒤의 창에 전달됩니다.");
        CodexTitle.Text = UiText.T("Codex connection", "Codex 연결");
        CodexHint.Text = UiText.T("Uses the Codex CLI signed in on this PC. No browser extension is needed.", "이 PC에 로그인된 Codex CLI를 사용합니다. 브라우저 확장은 필요 없습니다.");
        CodexExeLabel.Text = UiText.CodexExecutable;
        CodexExeBox.Text = settings.CodexExePath ?? "";
        AutoDetectHint.Text = UiText.T("Detect automatically", "자동으로 찾기");
        CodexPathHint.Text = UiText.T("Leave empty for automatic detection. Set an absolute path only if Codex cannot be found.", "보통은 비워 두면 됩니다. Codex를 찾지 못할 때만 실행 파일의 전체 경로를 입력하세요.");
        RefreshScheduleTitle.Text = UiText.T("Automatic refresh · every 5 minutes", "자동 확인 · 5분마다");
        RefreshScheduleHint.Text = UiText.T("You can refresh at any time from the usage card.", "사용량 카드에서 언제든 새로고침할 수 있습니다.");
        LogsButton.Content = UiText.T("Open logs", "로그 열기");
        SaveButton.Content = new System.Windows.Controls.TextBlock
        {
            Text = UiText.Save, Foreground = (Brush)FindResource("OnAccentBrush")
        };
        CancelButton.Content = UiText.T("Cancel", "취소");
        CloseSettingsButton.ToolTip = UiText.Close;
        SetName(CloseSettingsButton, UiText.Close);
        SetName(ThemeBox, ThemeLabel.Text); SetName(LanguageBox, LanguageLabel.Text);
        SetName(IconBox, IconLabel.Text); SetName(StartupBox, StartupLabel.Text);
        SetName(WidgetBox, WidgetLabel.Text); SetName(WidgetTopBox, WidgetTopLabel.Text);
        SetName(WidgetClickThroughBox, WidgetClickThroughLabel.Text);
        SetName(WidgetOpacityBox, WidgetOpacityLabel.Text); SetName(CodexExeBox, CodexExeLabel.Text);
        SourceInitialized += (_, _) => FitWorkArea();
    }

    private static void SetName(DependencyObject control, string text) => AutomationProperties.SetName(control, text);

    private void FitWorkArea()
    {
        var work = SystemParameters.WorkArea;
        MaxHeight = Math.Max(320, work.Height - 24);
        MinHeight = Math.Min(MinHeight, MaxHeight);
        Height = Math.Min(Height, MaxHeight);
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.CodexExePath = string.IsNullOrWhiteSpace(CodexExeBox.Text) ? null : CodexExeBox.Text.Trim();
        _settings.Theme = (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2);
        _settings.UiLanguage = LanguageBox.SelectedIndex == 0 ? UiLanguage.Korean : UiLanguage.English;
        _settings.TrayIconStyle = (TrayIconStyle)Math.Clamp(IconBox.SelectedIndex, 0, 1);
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.TaskbarStatusEnabled = false;
        _settings.FloatingWidgetEnabled = WidgetBox.IsChecked == true;
        _settings.WidgetOpacity = WidgetOpacityBox.Value;
        _settings.WidgetAlwaysOnTop = WidgetTopBox.IsChecked == true;
        _settings.WidgetClickThrough = WidgetClickThroughBox.IsChecked == true;
        Saved?.Invoke(_settings);
        Close();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateOpacityText();
    private void UpdateOpacityText()
    {
        if (WidgetOpacityValue is not null && WidgetOpacityBox is not null)
            WidgetOpacityValue.Text = $"{Math.Round(WidgetOpacityBox.Value * 100)}%";
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }
    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke();
}
