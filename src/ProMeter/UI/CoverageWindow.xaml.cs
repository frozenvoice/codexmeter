namespace ProMeter.UI;

public partial class CoverageWindow : Window
{
    public CoverageWindow(CoverageInfo coverage)
    {
        InitializeComponent();
        Headline.Text = $"Coverage: {coverage.SummaryLabel} ({coverage.Confidence})";
        NormalText.Text = $"Normal chats       {(coverage.NormalChats ? "✓" : "—")}";
        ArchivedText.Text = $"Archived chats     {(coverage.ArchivedChats ? "✓" : "—")}";
        ProjectsText.Text = $"Projects           {(coverage.Projects ? "✓" : "—")}";
        ResetText.Text = $"Reset time         {(coverage.ResetTimeAuthoritative ? "server" : "estimated")}";
        NotesText.Text = coverage.Notes ?? "Temporary and deleted chats cannot be reconstructed from account history.";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
