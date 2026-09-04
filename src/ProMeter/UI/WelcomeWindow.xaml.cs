namespace ProMeter.UI;

public partial class WelcomeWindow : Window
{
    public SubscriptionPreset SelectedPreset => PlanBox.SelectedIndex switch
    {
        1 => SubscriptionPreset.Pro200,
        2 => SubscriptionPreset.Custom,
        _ => SubscriptionPreset.Pro100
    };

    public WelcomeWindow()
    {
        InitializeComponent();
    }

    public event Action? SignInRequested;

    public void SetBusy(string text)
    {
        ProgressText.Text = text;
        PrimaryButton.IsEnabled = false;
    }

    public void SetReady(string text)
    {
        ProgressText.Text = text;
        PrimaryButton.Content = "Finish";
        PrimaryButton.IsEnabled = true;
        PrimaryButton.Click -= OnPrimaryClick;
        PrimaryButton.Click += (_, _) => DialogResult = true;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => SignInRequested?.Invoke();
}
