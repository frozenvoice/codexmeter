namespace ProMeter.UI;

public partial class CoverageWindow : Window
{
    public CoverageWindow(CoverageInfo coverage)
    {
        InitializeComponent();
        Title = UiText.DataStatus;
        Headline.Text = $"{UiText.DataStatus}: {DisplayFormatting.OverallCollectionLabel(coverage)}";
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
            : coverage.Notes ?? UiText.TemporaryDeletedNote;
        CloseButton.Content = UiText.Close;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
