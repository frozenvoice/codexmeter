namespace ProMeter.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public event Action<AppSettings>? Saved;
    public event Action? ImportRequested;
    public event Action<string>? ExportRequested;
    public event Action? OpenLogsRequested;
    public event Action<string?, string?>? CompanionRegisterRequested;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ApplyLocalizedTexts();
        PlanBox.SelectedIndex = (int)settings.PlanPreset;
        WeeklyBox.Text = settings.WeeklyProQuota.ToString(CultureInfo.InvariantCulture);
        DailyBox.Text = settings.DailyProQuota?.ToString(CultureInfo.InvariantCulture) ?? "";
        SolDailyBox.Text = settings.SolProDailyQuota?.ToString(CultureInfo.InvariantCulture) ?? "";
        CombinedBox.Text = settings.CombinedDailyQuota?.ToString(CultureInfo.InvariantCulture) ?? "";
        ReasoningBox.Text = settings.ReasoningQuota?.ToString(CultureInfo.InvariantCulture) ?? "";
        WeekdayBox.SelectedIndex = (int)settings.ResetWeekday;
        ResetTimeBox.Text = settings.ResetTime.ToString(@"hh\:mm");
        ResetAnchorBox.IsChecked = settings.ResetAnchorConfigured;
        TransportBox.SelectedIndex = (int)settings.AuthTransport;
        ChromeExtensionIdBox.Text = settings.ChromeExtensionId ?? settings.CompanionExtensionId ?? "";
        EdgeExtensionIdBox.Text = settings.EdgeExtensionId ?? "";
        PairingBox.Text = CompanionPairingStore.LoadOrCreate().Token;
        AutoSyncBox.IsChecked = settings.AutoSync;
        IntervalBox.Text = settings.SyncIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        StartupBox.IsChecked = settings.StartWithWindows;
        WidgetBox.IsChecked = settings.FloatingWidgetEnabled;
        WidgetOpacityBox.Text = settings.WidgetOpacity.ToString("0.00", CultureInfo.InvariantCulture);
        WidgetTopBox.IsChecked = settings.WidgetAlwaysOnTop;
        WidgetClickThroughBox.IsChecked = settings.WidgetClickThrough;
        TaskbarStatusBox.IsChecked = settings.TaskbarStatusEnabled;
        CodexExeBox.Text = settings.CodexExePath ?? "";
        ThemeBox.SelectedIndex = (int)settings.Theme;
        LanguageBox.SelectedIndex = settings.UiLanguage == UiLanguage.Korean ? 0 : 1;
        IconBox.SelectedIndex = (int)settings.TrayIconStyle;
        FlyoutCloseBox.IsChecked = settings.FlyoutCloseOnDeactivate;
        N20.IsChecked = settings.NotifyAt20;
        N10.IsChecked = settings.NotifyAt10;
        NEx.IsChecked = settings.NotifyExhausted;
        NReset.IsChecked = settings.NotifyReset;
        NErr.IsChecked = settings.NotifySyncError;
        HistoricalBox.IsChecked = settings.ImportHistoricalStatistics;
    }

    private void OnPlanChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PlanBox.SelectedIndex < 0 || WeeklyBox is null)
        {
            return;
        }

        var preset = (SubscriptionPreset)PlanBox.SelectedIndex;
        if (preset == SubscriptionPreset.Custom)
        {
            return;
        }

        var temp = new AppSettings();
        temp.ApplyPreset(preset);
        WeeklyBox.Text = temp.WeeklyProQuota.ToString(CultureInfo.InvariantCulture);
        DailyBox.Text = temp.DailyProQuota?.ToString(CultureInfo.InvariantCulture) ?? "";
        SolDailyBox.Text = temp.SolProDailyQuota?.ToString(CultureInfo.InvariantCulture) ?? "";
        CombinedBox.Text = temp.CombinedDailyQuota?.ToString(CultureInfo.InvariantCulture) ?? "";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var resetTime = _settings.ResetTime;
        if (TimeSpan.TryParse(ResetTimeBox.Text, out var parsedTime))
        {
            resetTime = parsedTime;
        }

        SettingsApplication.Apply(_settings, new SettingsEdit
        {
            PlanPreset = (SubscriptionPreset)PlanBox.SelectedIndex,
            WeeklyProQuota = ParseInt(WeeklyBox.Text, 50),
            DailyProQuota = ParseNullable(DailyBox.Text),
            SolProDailyQuota = ParseNullable(SolDailyBox.Text),
            CombinedDailyQuota = ParseNullable(CombinedBox.Text),
            ReasoningQuota = ParseNullable(ReasoningBox.Text),
            ResetWeekday = (DayOfWeek)WeekdayBox.SelectedIndex,
            ResetTime = resetTime,
            ResetAnchorConfigured = ResetAnchorBox.IsChecked == true,
            AuthTransport = (AuthTransportKind)Math.Clamp(TransportBox.SelectedIndex, 0, 2),
            ChromeExtensionId = TrimOrNull(ChromeExtensionIdBox.Text),
            EdgeExtensionId = TrimOrNull(EdgeExtensionIdBox.Text),
            CompanionExtensionId = TrimOrNull(ChromeExtensionIdBox.Text) ?? TrimOrNull(EdgeExtensionIdBox.Text),
            AutoSync = AutoSyncBox.IsChecked == true,
            SyncIntervalMinutes = ParseInt(IntervalBox.Text, 15),
            StartWithWindows = StartupBox.IsChecked == true,
            FloatingWidgetEnabled = WidgetBox.IsChecked == true,
            TaskbarStatusEnabled = TaskbarStatusBox.IsChecked == true,
            CodexExePath = TrimOrNull(CodexExeBox.Text),
            Theme = (AppTheme)ThemeBox.SelectedIndex,
            TrayIconStyle = (TrayIconStyle)IconBox.SelectedIndex,
            UiLanguage = LanguageBox.SelectedIndex == 0 ? UiLanguage.Korean : UiLanguage.English,
            FlyoutCloseOnDeactivate = FlyoutCloseBox.IsChecked == true,
            NotifyAt20 = N20.IsChecked == true,
            NotifyAt10 = N10.IsChecked == true,
            NotifyExhausted = NEx.IsChecked == true,
            NotifyReset = NReset.IsChecked == true,
            NotifySyncError = NErr.IsChecked == true,
            ImportHistoricalStatistics = HistoricalBox.IsChecked == true,
            WidgetOpacity = ParseOpacity(WidgetOpacityBox.Text, _settings.WidgetOpacity),
            WidgetAlwaysOnTop = WidgetTopBox.IsChecked == true,
            WidgetClickThrough = WidgetClickThroughBox.IsChecked == true
        });
        Saved?.Invoke(_settings);
        Close();
    }

    private void OnRegisterCompanion(object sender, RoutedEventArgs e) =>
        CompanionRegisterRequested?.Invoke(TrimOrNull(ChromeExtensionIdBox.Text), TrimOrNull(EdgeExtensionIdBox.Text));

    private void ApplyLocalizedTexts()
    {
        Title = UiText.SettingsTitle;
        TitleText.Text = UiText.Settings;
        PlanTitle.Text = UiText.Plan;
        SetCombo(PlanBox, UiText.Plan100Short, UiText.Plan200Short, UiText.PlanCustomShort);
        WeeklyLabel.Text = UiText.WeeklyProQuota;
        DailyLabel.Text = UiText.DailyProQuota;
        SolDailyLabel.Text = UiText.SolProDailyQuota;
        CombinedLabel.Text = UiText.CombinedDailyQuota;
        ReasoningLabel.Text = UiText.ReasoningQuota;
        ResetTitle.Text = UiText.ResetAnchor;
        ResetHint.Text = UiText.ResetAnchorHint;
        SetCombo(WeekdayBox,
            UiText.Weekday(DayOfWeek.Sunday),
            UiText.Weekday(DayOfWeek.Monday),
            UiText.Weekday(DayOfWeek.Tuesday),
            UiText.Weekday(DayOfWeek.Wednesday),
            UiText.Weekday(DayOfWeek.Thursday),
            UiText.Weekday(DayOfWeek.Friday),
            UiText.Weekday(DayOfWeek.Saturday));
        ResetAnchorBox.Content = UiText.ResetAnchorCheck;
        ConnectionTitle.Text = UiText.Connection;
        ConnectionHint.Text = UiText.ConnectionHint;
        SetCombo(TransportBox, UiText.TransportCompanion, UiText.TransportWebView, UiText.TransportExport);
        ChromeIdLabel.Text = UiText.ChromeExtensionId;
        EdgeIdLabel.Text = UiText.EdgeExtensionId;
        PairingHint.Text = UiText.PairingTokenHint;
        RegisterButton.Content = UiText.RegisterNativeHost;
        AppTitle.Text = UiText.AppSection;
        AppHint.Text = UiText.AppRiskHint;
        AutoSyncBox.Content = UiText.AutomaticSync;
        IntervalLabel.Text = UiText.SyncInterval;
        StartupBox.Content = UiText.StartWithWindows;
        WidgetBox.Content = UiText.FloatingWidget;
        WidgetHint.Text = UiText.FloatingWidgetHint;
        WidgetOpacityLabel.Text = UiText.WidgetOpacity;
        WidgetTopBox.Content = UiText.WidgetAlwaysOnTop;
        WidgetClickThroughBox.Content = UiText.WidgetClickThrough;
        WidgetClickThroughHint.Text = UiText.WidgetClickThroughHint;
        TaskbarStatusBox.Content = UiText.TaskbarStatusEnabled;
        TaskbarStatusHint.Text = UiText.TaskbarStatusHint;
        CodexExeLabel.Text = UiText.CodexExePath;
        CodexExeHint.Text = UiText.CodexExePathHint;
        LanguageLabel.Text = UiText.LanguageCaption;
        SetCombo(LanguageBox, UiText.Korean, UiText.English);
        SetCombo(ThemeBox, UiText.ThemeSystem, UiText.ThemeLight, UiText.ThemeDark);
        SetCombo(IconBox, UiText.TrayRemainingNumber, UiText.TrayProgressRing);
        FlyoutCloseBox.Content = UiText.CloseFlyoutOnDeactivate;
        NotificationsTitle.Text = UiText.Notifications;
        N20.Content = UiText.Notify20;
        N10.Content = UiText.Notify10;
        NEx.Content = UiText.NotifyExhausted;
        NReset.Content = UiText.NotifyReset;
        NErr.Content = UiText.NotifySyncError;
        DataTitle.Text = UiText.Data;
        ImportButton.Content = UiText.ImportConversations;
        ExportJsonButton.Content = UiText.ExportJson;
        ExportCsvButton.Content = UiText.ExportCsv;
        OpenLogsButton.Content = UiText.OpenLogs;
        HistoricalBox.Content = UiText.ImportOlder;
        SaveButton.Content = UiText.Save;
    }

    private static void SetCombo(System.Windows.Controls.ComboBox box, params string[] items)
    {
        var selected = box.SelectedIndex;
        for (var i = 0; i < items.Length && i < box.Items.Count; i++)
        {
            if (box.Items[i] is System.Windows.Controls.ComboBoxItem item)
            {
                item.Content = items[i];
            }
        }

        box.SelectedIndex = selected;
    }

    private static string? TrimOrNull(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void OnImport(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();
    private void OnExportJson(object sender, RoutedEventArgs e) => ExportRequested?.Invoke("json");
    private void OnExportCsv(object sender, RoutedEventArgs e) => ExportRequested?.Invoke("csv");
    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke();

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, out var value) ? value : fallback;

    private static int? ParseNullable(string? text) =>
        int.TryParse(text, out var value) ? value : null;

    private static double ParseOpacity(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0.3, 1)
            : fallback;
}
