using System.Windows.Controls;

namespace ProMeter.UI;

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
            SignInButton.Content = "Sign in again";
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
            ProgressText.Text = "Companion connected. Run first manual sync when you are ready, or Finish to configure later.";
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
