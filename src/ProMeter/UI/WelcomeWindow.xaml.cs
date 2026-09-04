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

    public bool StartWithWindowsOptIn => StartupBox.IsChecked == true;
    public bool AutoSyncOptIn => AutoSyncBox.IsChecked == true;

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    public event Action? SignInRequested;
    public event Action? SyncRequested;

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
        SyncButton.IsEnabled = presentation.AllowRetrySync;
        FinishButton.IsEnabled = presentation.AllowFinish;
        if (presentation.AllowSignInAgain)
        {
            SignInButton.Content = "Sign in again";
        }
    }

    public void MarkSignedIn(string text)
    {
        ProgressText.Text = text;
        SignInButton.IsEnabled = true;
        SyncButton.IsEnabled = SelectedTransport != AuthTransportKind.DataExport;
        FinishButton.IsEnabled = SelectedTransport == AuthTransportKind.DataExport;
    }

    public void SetCancelled(string text)
    {
        ProgressText.Text = text;
        SignInButton.IsEnabled = true;
        SyncButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
    }

    private void OnSignIn(object sender, RoutedEventArgs e) => SignInRequested?.Invoke();
    private void OnSync(object sender, RoutedEventArgs e) => SyncRequested?.Invoke();
    private void OnFinish(object sender, RoutedEventArgs e) => DialogResult = true;
}
