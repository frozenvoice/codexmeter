using System.Windows.Controls;

namespace CycleArc.UI;

public partial class WelcomeWindow : Window
{
    public SubscriptionPreset SelectedPreset => PlanBox.SelectedIndex switch
    {
        1 => SubscriptionPreset.Pro200,
        2 => SubscriptionPreset.Custom,
        _ => SubscriptionPreset.Pro100
    };

    public AuthTransportKind SelectedTransport => TransportBox.SelectedIndex switch
    {
        1 => AuthTransportKind.WebView2,
        2 => AuthTransportKind.DataExport,
        _ => AuthTransportKind.BrowserCompanion
    };

    public string? ChromeExtensionId => string.IsNullOrWhiteSpace(ChromeExtensionIdBox.Text) ? null : ChromeExtensionIdBox.Text.Trim();
    public string? EdgeExtensionId => string.IsNullOrWhiteSpace(EdgeExtensionIdBox.Text) ? null : EdgeExtensionIdBox.Text.Trim();
    public bool StartWithWindowsOptIn => StartupBox.IsChecked == true;
    public bool AutoSyncOptIn => AutoSyncBox.IsChecked == true;
    public bool CompanionRegistered { get; private set; }
    public bool CompanionConnected { get; private set; }

    public WelcomeWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();
    }

    private void ApplyLocalizedTexts()
    {
        Title = UiText.WelcomeTitle;
        TitleText.Text = UiText.WelcomeTitle;
        SubtitleText.Text = UiText.WelcomeSubtitle;
        Step1Title.Text = UiText.WelcomeStep1;
        Step1Body.Text = UiText.WelcomeStep1Body;
        Step2Title.Text = UiText.WelcomeStep2;
        SetCombo(TransportBox, UiText.WelcomeTransportCompanion, UiText.WelcomeTransportWebView, UiText.WelcomeTransportExport);
        SocialHint.Text = UiText.WelcomeSocialHint;
        CompanionHint.Text = UiText.WelcomeCompanionHint;
        OpenExtensionButton.Content = UiText.OpenExtensionFolder;
        ChromeIdLabel.Text = UiText.WelcomeChromeId;
        EdgeIdLabel.Text = UiText.WelcomeEdgeId;
        RegisterCompanionButton.Content = UiText.RegisterSelectedHost;
        CompanionStateText.Text = UiText.CompanionNotInstalled;
        OpenChatGptButton.Content = UiText.OpenChatGpt;
        Step3Title.Text = UiText.WelcomeStep3;
        SetCombo(PlanBox, UiText.Plan100, UiText.Plan200, UiText.PlanCustom);
        Step4Title.Text = UiText.WelcomeStep4;
        OptInHint.Text = UiText.WelcomeOptInHint;
        StartupBox.Content = UiText.StartWithWindows;
        AutoSyncBox.Content = UiText.AutomaticSync;
        Step5Title.Text = UiText.WelcomeStep5;
        ProgressText.Text = UiText.WelcomeSignInHint;
        FinishButton.Content = UiText.Finish;
        SyncButton.Content = UiText.RunFirstManualSync;
        SignInButton.Content = UiText.SignInConnect;
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

    public event Action? SignInRequested;
    public event Action? SyncRequested;
    public event Action? OpenExtensionFolderRequested;
    public event Action<string?, string?>? RegisterCompanionRequested;
    public event Action? OpenChatGptRequested;

    public void SetBusy(string text)
    {
        ProgressText.Text = text;
        SignInButton.IsEnabled = false;
        SyncButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
    }

    public void ApplyOutcome(OnboardingPresentation presentation)
    {
        ProgressText.Text = presentation.Message;
        SignInButton.IsEnabled = true;
        SyncButton.IsEnabled = presentation.AllowRetrySync && (SelectedTransport != AuthTransportKind.BrowserCompanion || CompanionConnected);
        FinishButton.IsEnabled = presentation.AllowFinish || CompanionRegistered;
        if (presentation.AllowSignInAgain)
        {
            SignInButton.Content = UiText.SignInAgain;
        }
    }

    public void MarkSignedIn(string text)
    {
        ProgressText.Text = text;
        SignInButton.IsEnabled = true;
        SyncButton.IsEnabled = SelectedTransport != AuthTransportKind.DataExport
            && (SelectedTransport != AuthTransportKind.BrowserCompanion || CompanionConnected);
        FinishButton.IsEnabled = SelectedTransport == AuthTransportKind.DataExport || CompanionRegistered;
    }

    public void SetCancelled(string text)
    {
        ProgressText.Text = text;
        SignInButton.IsEnabled = true;
        SyncButton.IsEnabled = false;
        FinishButton.IsEnabled = CompanionRegistered;
    }

    public void SetCompanionState(string state, bool registered, bool connected)
    {
        CompanionRegistered = registered;
        CompanionConnected = connected;
        CompanionStateText.Text = state;
        SyncButton.IsEnabled = connected && SelectedTransport == AuthTransportKind.BrowserCompanion;
        FinishButton.IsEnabled = registered || SelectedTransport != AuthTransportKind.BrowserCompanion;
        if (connected)
        {
            ProgressText.Text = UiText.CompanionConnectedReady;
        }
    }

    private void OnTransportChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CompanionPanel is null)
        {
            return;
        }

        CompanionPanel.Visibility = SelectedTransport == AuthTransportKind.BrowserCompanion ? Visibility.Visible : Visibility.Collapsed;
        if (SelectedTransport == AuthTransportKind.BrowserCompanion)
        {
            FinishButton.IsEnabled = CompanionRegistered;
            SyncButton.IsEnabled = CompanionConnected;
        }
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => SignInRequested?.Invoke();
    private void OnSync(object sender, RoutedEventArgs e) => SyncRequested?.Invoke();
    private void OnFinish(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnOpenExtension(object sender, RoutedEventArgs e) => OpenExtensionFolderRequested?.Invoke();
    private void OnRegisterCompanion(object sender, RoutedEventArgs e) =>
        RegisterCompanionRequested?.Invoke(ChromeExtensionId, EdgeExtensionId);
    private void OnOpenChatGpt(object sender, RoutedEventArgs e) => OpenChatGptRequested?.Invoke();
}
