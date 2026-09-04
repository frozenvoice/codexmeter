using System.Diagnostics;
using System.Windows.Navigation;

namespace ProMeter.UI;

public partial class AboutWindow : Window
{
    public AboutWindow(string version, string dataSource)
    {
        InitializeComponent();
        VersionText.Text = "Version " + version;
        SourceText.Text = "Data source status: " + dataSource;
    }

    private void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
