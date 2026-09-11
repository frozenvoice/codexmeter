using System.Diagnostics;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace CycleArc.UI;

public partial class AboutWindow : Window
{
    public AboutWindow(string version, string dataSource)
    {
        InitializeComponent();
        Title = UiText.AboutTitle;
        SubtitleText.Text = UiText.T("Codex and Claude account usage monitor", "Codex·Claude 계정 사용량 모니터");
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
