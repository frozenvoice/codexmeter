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
        ResetText.Text = $"Reset time         {coverage.ResetAnchorSource switch { ResetAnchorSource.Server => "Server reset", ResetAnchorSource.UserConfigured => "User-configured reset", _ => "Estimated reset" }}";
        CountConfidenceText.Text = $"Count confidence   {coverage.CountConfidence}";
        ResetConfidenceText.Text = $"Reset confidence   {coverage.ResetConfidence}";
        BranchesText.Text = $"Branches included  {(coverage.BranchesIncluded ? "yes" : "unknown")}";
        IncompleteText.Text = coverage.IndexIncomplete || coverage.ConversationIncomplete || coverage.FailedConversations > 0
            ? $"Incomplete         index={coverage.IndexIncomplete} conversations={coverage.FailedConversations}"
            : "Incomplete         no";
        NotesText.Text = coverage.Notes ?? "Temporary and deleted chats cannot be reconstructed from account history.";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
