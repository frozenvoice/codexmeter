using System.Diagnostics;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace ProMeter.UI;

public partial class AboutWindow : Window
{
    public AboutWindow(string version, string dataSource)
    {
        InitializeComponent();
        Title = UiText.T("About ProMeter", "ProMeter 정보");
        SubtitleText.Text = UiText.AboutSubtitle;
        GitHubLink.Inlines.Clear();
        GitHubLink.Inlines.Add(new Run(UiText.GitHubRepository));
        VersionText.Text = UiText.VersionPrefix + version;
        SourceText.Text = UiText.DataSourceStatus + dataSource;
        CloseButton.Content = UiText.Close;
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
