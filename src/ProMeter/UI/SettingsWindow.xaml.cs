namespace ProMeter.UI;

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
        CodexTitle.Text = "CODEX";
        CodexHint.Text = UiText.T("Uses the signed-in Codex CLI to check account limits every 5 minutes. No browser extension is needed. Leave the path empty for automatic detection.",
            "로그인된 Codex CLI로 5분마다 계정 한도를 확인합니다. 브라우저 확장은 필요 없습니다. 경로를 비우면 자동으로 찾습니다.");
        CodexExeLabel.Text = UiText.CodexExecutable;
        CodexExeBox.Text = settings.CodexExePath ?? "";
        AppearanceTitle.Text = UiText.T("DISPLAY", "화면");
        ThemeLabel.Text = UiText.T("Theme", "테마");
        ThemeBox.ItemsSource = new[] { UiText.T("System", "시스템"), UiText.T("Light", "밝게"), UiText.T("Dark", "어둡게") };
        ThemeBox.SelectedIndex = (int)settings.Theme;
        LanguageLabel.Text = UiText.T("Language", "언어");
        LanguageBox.ItemsSource = new[] { "한국어", "English" };
        LanguageBox.SelectedIndex = settings.UiLanguage == UiLanguage.Korean ? 0 : 1;
        IconLabel.Text = UiText.T("Tray icon", "트레이 아이콘");
        IconBox.ItemsSource = new[] { UiText.T("Number", "숫자"), UiText.T("Progress ring", "진행률 링") };
        IconBox.SelectedIndex = (int)settings.TrayIconStyle;
        StartupBox.Content = UiText.StartWithWindows;
        StartupBox.IsChecked = settings.StartWithWindows;
        TaskbarStatusBox.Content = UiText.T("Show usage on the taskbar", "작업표시줄에 사용량 표시");
        TaskbarStatusBox.IsChecked = settings.TaskbarStatusEnabled;
        WidgetBox.Content = UiText.T("Show floating widget", "플로팅 위젯 표시");
        WidgetBox.IsChecked = settings.FloatingWidgetEnabled;
        WidgetOpacityLabel.Text = UiText.T("Widget opacity", "위젯 불투명도");
        WidgetOpacityBox.Value = settings.WidgetOpacity;
        WidgetTopBox.Content = UiText.T("Keep widget on top", "위젯을 항상 위에 표시");
        WidgetTopBox.IsChecked = settings.WidgetAlwaysOnTop;
        WidgetClickThroughBox.Content = UiText.T("Click through widget", "위젯 클릭 통과");
        WidgetClickThroughBox.IsChecked = settings.WidgetClickThrough;
        LogsButton.Content = UiText.OpenLogs;
        SaveButton.Content = UiText.T("Save", "저장");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.CodexExePath = string.IsNullOrWhiteSpace(CodexExeBox.Text) ? null : CodexExeBox.Text.Trim();
        _settings.Theme = (AppTheme)Math.Clamp(ThemeBox.SelectedIndex, 0, 2);
        _settings.UiLanguage = LanguageBox.SelectedIndex == 0 ? UiLanguage.Korean : UiLanguage.English;
        _settings.TrayIconStyle = (TrayIconStyle)Math.Clamp(IconBox.SelectedIndex, 0, 1);
        _settings.StartWithWindows = StartupBox.IsChecked == true;
        _settings.TaskbarStatusEnabled = TaskbarStatusBox.IsChecked == true;
        _settings.FloatingWidgetEnabled = WidgetBox.IsChecked == true;
        _settings.WidgetOpacity = WidgetOpacityBox.Value;
        _settings.WidgetAlwaysOnTop = WidgetTopBox.IsChecked == true;
        _settings.WidgetClickThrough = WidgetClickThroughBox.IsChecked == true;
        Saved?.Invoke(_settings);
        Close();
    }

    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke();
}
