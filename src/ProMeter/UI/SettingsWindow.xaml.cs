namespace ProMeter.UI;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _webViewOperationCancellation;
    private WebViewVerificationResult? _lastWebViewVerification;

    public event Action<AppSettings>? Saved;
    public event Action? ImportRequested;
    public event Action<string>? ExportRequested;
    public event Action? OpenLogsRequested;
    public event Action<string?, string?>? CompanionRegisterRequested;
    public Func<CancellationToken, Task<WebViewDiagnosticResult>>? WebViewDiagnosticRequested { get; set; }
    public Func<CancellationToken, Task<WebViewVerificationResult>>? FullWebViewVerificationRequested { get; set; }
    public Func<WebViewVerificationResult, bool, bool>? UseWebViewDefaultRequested { get; set; }

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
        Closed += (_, _) => CancelWebViewOperation();
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
            ImportHistoricalStatistics = HistoricalBox.IsChecked == true
        });
        Saved?.Invoke(_settings);
        Close();
    }

    private void OnRegisterCompanion(object sender, RoutedEventArgs e) =>
        CompanionRegisterRequested?.Invoke(TrimOrNull(ChromeExtensionIdBox.Text), TrimOrNull(EdgeExtensionIdBox.Text));

    private async void OnTestWebView(object sender, RoutedEventArgs e)
    {
        if (WebViewDiagnosticRequested is null || _webViewOperationCancellation is not null)
        {
            return;
        }

        _lastWebViewVerification = null;
        FullWebViewVerificationButton.Visibility = Visibility.Collapsed;
        UseWebViewDefaultButton.Visibility = Visibility.Collapsed;
        WebViewComparisonText.Visibility = Visibility.Collapsed;
        SetTechnicalDetail(null);
        _webViewOperationCancellation = new CancellationTokenSource();
        TestWebViewButton.IsEnabled = false;
        TestWebViewButton.Content = UiText.TestingWebView2;
        WebViewResultText.Text = UiText.TestingWebView2;
        WebViewResultText.Visibility = Visibility.Visible;
        try
        {
            var result = await WebViewDiagnosticRequested(_webViewOperationCancellation.Token);
            WebViewResultText.Text = UiText.WebViewDiagnosticMessage(result.Status);
            SetTechnicalDetail(result.TechnicalDetail);
            FullWebViewVerificationButton.Visibility = result.Passed ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            WebViewResultText.Text = UiText.WebViewDiagnosticCancelled;
            SetTechnicalDetail(null);
        }
        catch
        {
            WebViewResultText.Text = UiText.WebViewDiagnosticFailApi;
            SetTechnicalDetail(UiText.WebViewDiagnosticTechnical("diagnostic", reason: "unavailable"));
        }
        finally
        {
            FinishWebViewOperation();
            TestWebViewButton.Content = UiText.TestWebView2;
        }
    }

    private async void OnFullWebViewVerification(object sender, RoutedEventArgs e)
    {
        if (FullWebViewVerificationRequested is null || _webViewOperationCancellation is not null)
        {
            return;
        }

        _webViewOperationCancellation = new CancellationTokenSource();
        TestWebViewButton.IsEnabled = false;
        FullWebViewVerificationButton.IsEnabled = false;
        UseWebViewDefaultButton.Visibility = Visibility.Collapsed;
        SetTechnicalDetail(null);
        WebViewResultText.Text = UiText.RunningFullWebViewVerification;
        WebViewResultText.Visibility = Visibility.Visible;
        WebViewComparisonText.Visibility = Visibility.Collapsed;
        try
        {
            var result = await FullWebViewVerificationRequested(_webViewOperationCancellation.Token);
            _lastWebViewVerification = result;
            WebViewResultText.Text = UiText.WebViewVerificationMessage(result.Status);
            if (result.WebViewCount is int webViewCount && result.Difference is int difference)
            {
                WebViewComparisonText.Text = string.Join(
                    Environment.NewLine,
                    UiText.BrowserCompanionCount(result.BrowserCompanionCount),
                    UiText.WebViewCount(webViewCount),
                    UiText.VerificationDifference(difference),
                    UiText.BrowserBaselineReconstructed(result.BrowserCompanionEstimated));
                WebViewComparisonText.Visibility = Visibility.Visible;
            }

            SetTechnicalDetail(result.TechnicalDetail);
            UseWebViewDefaultButton.Visibility = result.CanUseWebViewAsDefault
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            WebViewResultText.Text = UiText.WebViewVerificationCancelled;
            SetTechnicalDetail(null);
        }
        catch
        {
            WebViewResultText.Text = UiText.WebViewVerificationFailed;
            SetTechnicalDetail(UiText.WebViewVerificationUnavailableTechnical);
        }
        finally
        {
            FinishWebViewOperation();
            FullWebViewVerificationButton.IsEnabled = true;
        }
    }

    private void OnUseWebViewDefault(object sender, RoutedEventArgs e)
    {
        if (_lastWebViewVerification is null || UseWebViewDefaultRequested is null)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            UiText.UseWebViewAsDefaultConfirmation,
            UiText.ProductName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) == MessageBoxResult.Yes;
        var applied = UseWebViewDefaultRequested(_lastWebViewVerification, confirmed);
        if (applied)
        {
            TransportBox.SelectedIndex = (int)AuthTransportKind.WebView2;
        }

        MessageBox.Show(
            applied ? UiText.UseWebViewAsDefaultSucceeded : UiText.UseWebViewAsDefaultFailed,
            UiText.ProductName);
    }

    private void SetTechnicalDetail(string? detail)
    {
        WebViewTechnicalDetailText.Text = string.IsNullOrWhiteSpace(detail)
            ? ""
            : UiText.TechnicalDetail(detail);
        WebViewTechnicalDetailText.Visibility = string.IsNullOrWhiteSpace(detail)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void FinishWebViewOperation()
    {
        _webViewOperationCancellation?.Dispose();
        _webViewOperationCancellation = null;
        TestWebViewButton.IsEnabled = true;
    }

    private void CancelWebViewOperation()
    {
        _webViewOperationCancellation?.Cancel();
    }

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
        TestWebViewButton.Content = UiText.TestWebView2;
        FullWebViewVerificationButton.Content = UiText.RunFullWebViewVerification;
        UseWebViewDefaultButton.Content = UiText.UseWebViewAsDefault;
        AppTitle.Text = UiText.AppSection;
        AppHint.Text = UiText.AppRiskHint;
        AutoSyncBox.Content = UiText.AutomaticSync;
        IntervalLabel.Text = UiText.SyncInterval;
        StartupBox.Content = UiText.StartWithWindows;
        WidgetBox.Content = UiText.FloatingWidget;
        TaskbarStatusBox.Content = UiText.TaskbarStatusEnabled;
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
}
