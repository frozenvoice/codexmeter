using System.Windows.Controls;
using ProMeter.Codex;

namespace ProMeter.UI;

public partial class CoverageWindow : Window
{
    public CoverageWindow(CoverageInfo coverage, CodexQuotaSnapshot? codex = null, bool executableFound = false)
    {
        InitializeComponent();
        Title = UiText.DataStatus;
        Headline.Text = $"{UiText.DataStatus}: {DisplayFormatting.CoverageCompactLabel(coverage)}";
        DisclaimerText.Text = UiText.CoverageDisclaimer;
        NormalText.Text = $"{UiText.NormalChats}    {DisplayFormatting.CollectionStateLabel(coverage.NormalIndexState)}";
        ArchivedText.Text = $"{UiText.ArchivedChats}    {DisplayFormatting.CollectionStateLabel(coverage.ArchivedIndexState)}";
        ProjectsText.Text = $"{UiText.Projects}    {DisplayFormatting.CollectionStateLabel(coverage.ProjectsIndexState)}";
        BodiesText.Text = $"{UiText.ConversationBodies}    {coverage.LoadedConversations} {UiText.Successful}, {coverage.FailedConversations} {UiText.UniqueFailed}";
        TemporaryText.Text = $"{UiText.TemporaryChats}    {UiText.CannotReconstruct}";
        DeletedText.Text = $"{UiText.DeletedChats}    {UiText.CannotReconstruct}";
        CountBasisText.Text = $"{UiText.CountBasis}    {(coverage.QuotaMetadataAuthoritative ? UiText.CountBasisServer : UiText.CountBasisReconstructed)}";
        CountConfidenceText.Text = $"{UiText.CountConfidence}    {DisplayFormatting.CountConfidenceLabel(coverage.CountConfidence)}";
        ResetBasisText.Text = $"{UiText.ResetBasis}    {coverage.ResetAnchorSource switch { ResetAnchorSource.Server => UiText.ResetBasisServer, ResetAnchorSource.UserConfigured => UiText.ResetBasisUser, _ => UiText.ResetBasisEstimated }}";
        BranchesText.Text = $"{UiText.BranchCoverage}    {(coverage.BranchesIncluded ? UiText.BranchIncluded : UiText.BranchUnknown)}";
        NotesText.Text = coverage.Notes == SyncEngine.MissingAssistantUsageDiagnostic
            ? UiText.HistoryLoadedWithoutUsage
            : coverage.FailureSummary.HasConversationFailures
                ? UiText.TemporaryDeletedNote
                : coverage.Notes ?? UiText.TemporaryDeletedNote;
        FailurePanel.Children.Clear();
        foreach (var line in DisplayFormatting.CoverageFailureDetailLines(coverage))
        {
            FailurePanel.Children.Add(new TextBlock
            {
                Text = line,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)FindResource("TextBrush"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        FailurePanel.Visibility = FailurePanel.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CodexTitle.Text = CodexDisplayFormatting.SectionTitle;
        CodexText.Text = string.Join(
            Environment.NewLine,
            CodexDisplayFormatting.DiagnosticLines(
                codex ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable),
                executableFound));
        CloseButton.Content = UiText.Close;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
