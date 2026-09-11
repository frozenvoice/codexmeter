using System.Windows.Controls;
using CycleArc.Codex;

namespace CycleArc.UI;

public partial class CoverageWindow : Window
{
    public CoverageWindow(QuotaSnapshot snapshot, CodexQuotaSnapshot? codex = null, bool executableFound = false)
        : this(DataStatusPresentation.From(snapshot, codex, executableFound))
    {
    }

    public CoverageWindow(DataStatusPresentation presentation)
    {
        InitializeComponent();
        Apply(presentation);
    }

    public void Apply(DataStatusPresentation presentation)
    {
        Title = UiText.DataStatus;
        Headline.Text = presentation.Headline;
        ChatGptServerText.Text = presentation.DefaultLines.Count > 0 ? presentation.DefaultLines[0] : "";
        CodexStatusRowText.Text = presentation.DefaultLines.Count > 1 ? presentation.DefaultLines[1] : "";
        HistoryStatsText.Text = presentation.DefaultLines.Count > 2 ? presentation.DefaultLines[2] : "";
        DisclaimerText.Text = presentation.Disclaimer;
        AdvancedHeader.Text = UiText.AdvancedDiagnostics;
        AdvancedExpander.IsExpanded = false;
        AdvancedPanel.Children.Clear();
        foreach (var line in presentation.AdvancedLines)
        {
            AdvancedPanel.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        CloseButton.Content = UiText.Close;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
