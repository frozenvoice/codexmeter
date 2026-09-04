namespace ProMeter.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public event Action<AppSettings>? Saved;
    public event Action? ImportRequested;
    public event Action<string>? ExportRequested;
    public event Action? OpenLogsRequested;
    public event Action<string?>? CompanionRegisterRequested;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
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
        ExtensionIdBox.Text = settings.CompanionExtensionId ?? "";
        PairingBox.Text = CompanionPairingStore.LoadOrCreate().Token;
        AutoSyncBox.IsChecked = settings.AutoSync;
        IntervalBox.Text = settings.SyncIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        StartupBox.IsChecked = settings.StartWithWindows;
        WidgetBox.IsChecked = settings.FloatingWidgetEnabled;
        ThemeBox.SelectedIndex = (int)settings.Theme;
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
            CompanionExtensionId = string.IsNullOrWhiteSpace(ExtensionIdBox.Text) ? null : ExtensionIdBox.Text.Trim(),
            AutoSync = AutoSyncBox.IsChecked == true,
            SyncIntervalMinutes = ParseInt(IntervalBox.Text, 15),
            StartWithWindows = StartupBox.IsChecked == true,
            FloatingWidgetEnabled = WidgetBox.IsChecked == true,
            Theme = (AppTheme)ThemeBox.SelectedIndex,
            TrayIconStyle = (TrayIconStyle)IconBox.SelectedIndex,
            FlyoutCloseOnDeactivate = FlyoutCloseBox.IsChecked == true,
            NotifyAt20 = N20.IsChecked == true,
            NotifyAt10 = N10.IsChecked == true,
            NotifyExhausted = NEx.IsChecked == true,
            NotifyReset = NReset.IsChecked == true,
            NotifySyncError = NErr.IsChecked == true,
            ImportHistoricalStatistics = HistoricalBox.IsChecked == true
        });
        Saved?.Invoke(_settings);
        Close();
    }

    private void OnRegisterCompanion(object sender, RoutedEventArgs e) =>
        CompanionRegisterRequested?.Invoke(string.IsNullOrWhiteSpace(ExtensionIdBox.Text) ? null : ExtensionIdBox.Text.Trim());

    private void OnImport(object sender, RoutedEventArgs e) => ImportRequested?.Invoke();
    private void OnExportJson(object sender, RoutedEventArgs e) => ExportRequested?.Invoke("json");
    private void OnExportCsv(object sender, RoutedEventArgs e) => ExportRequested?.Invoke("csv");
    private void OnOpenLogs(object sender, RoutedEventArgs e) => OpenLogsRequested?.Invoke();

    private static int ParseInt(string? text, int fallback) =>
        int.TryParse(text, out var value) ? value : fallback;

    private static int? ParseNullable(string? text) =>
        int.TryParse(text, out var value) ? value : null;
}
