using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexMeter.Codex;

namespace CodexMeter.UI;

public partial class MainWindow : Window
{
    public event Action? SyncRequested;
    public event Action? SettingsRequested;

    public MainWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();
    }

    public void ApplyLocalizedTexts()
    {
        SyncButton.Content = UiText.SyncNow;
        SettingsButton.Content = UiText.Settings;
        OverviewTab.Header = UiText.Overview;
        TrendsTab.Header = UiText.Trends;
        BreakdownTab.Header = UiText.Breakdown;
        CodexSectionTitle.Text = CodexDisplayFormatting.SectionTitle;
        ReasoningSectionTitle.Text = UiText.SolReasoning;
        StatusSectionTitle.Text = UiText.Status;
        TimeColumn.Header = UiText.TimeColumn;
        ModelColumn.Header = UiText.ModelColumn;
        RawColumn.Header = UiText.RawColumn;
        EffortColumn.Header = UiText.EffortColumn;
        FamilyColumn.Header = UiText.FamilyColumn;
        SourceColumn.Header = UiText.SourceColumn;
    }

    public void Bind(
        QuotaSnapshot snapshot,
        IReadOnlyList<UsageEvent> events,
        IReadOnlyList<DailyTrendPoint> trend,
        CodexQuotaSnapshot? codex = null)
    {
        ApplyLocalizedTexts();
        var presentation = ProStatusPresentation.From(snapshot);
        Headline.Text = presentation.Headline;
        var proLine = presentation.HasServerReset
            ? $"{UiText.GptPro}  {presentation.ProStateText}   ·   {UiText.ServerReset} {presentation.ResetText}"
            : $"{UiText.GptPro}  {presentation.ProStateText}";
        if (presentation.ShowHistoryLowerBound)
        {
            proLine += $"   ·   {presentation.ReconstructedLabel} {presentation.ConfirmedRequestsText}   ·   {presentation.HistoryLowerBoundCaption}";
            if (presentation.UnresolvedCount > 0)
            {
                proLine += $"   ·   {UiText.UnresolvedPending} {UiText.UnresolvedPendingCount(presentation.UnresolvedCount)}";
            }
        }

        ProStatusText.Text = proLine;
        var reset = DisplayFormatting.ResetDisplay(snapshot);
        PeriodText.Text = $"{DisplayFormatting.FormatDay(snapshot.PeriodStart)} – {DisplayFormatting.FormatDay(snapshot.PeriodEnd)}";
        StatusText.Text = $"{DisplayFormatting.FlyoutHeader(snapshot)}   ·   {reset.TimeLabel} {reset.TimeValue}{(reset.EstimateValue is null ? "" : $"   ·   {reset.EstimateLabel} {reset.EstimateValue}")}   ·   {UiText.DataStatus} {presentation.DataStatusText}   ·   {UiText.RemainingCount} {presentation.ExactRemainingText}";
        CodexText.Text = CodexDisplayFormatting.OverviewText(codex ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable));
        ReasonText.Text = snapshot.Reasoning.Limit is int limit
            ? $"{UiText.Today} {snapshot.Reasoning.Today}   {UiText.ThisWeek} {snapshot.Reasoning.ThisWeek}   {UiText.Medium} {snapshot.Reasoning.Medium}   {UiText.High} {snapshot.Reasoning.High}   {UiText.ExtraHigh} {snapshot.Reasoning.ExtraHigh}   {UiText.LimitInfo} {limit}   ·   {UiText.ReasoningReconstructedNote}"
            : $"{UiText.Today} {snapshot.Reasoning.Today}   {UiText.ThisWeek} {snapshot.Reasoning.ThisWeek}   {UiText.Medium} {snapshot.Reasoning.Medium}   {UiText.High} {snapshot.Reasoning.High}   {UiText.ExtraHigh} {snapshot.Reasoning.ExtraHigh}   {UiText.LimitInfo} {UiText.NotAvailable}   ·   {UiText.ReasoningReconstructedNote}";
        ModelList.Items.Clear();
        foreach (var model in snapshot.ModelBreakdown)
        {
            ModelList.Items.Add(new TextBlock
            {
                Text = $"{model.NormalizedModel,-24} {model.Count}",
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Segoe UI"),
                Margin = new Thickness(0, 4, 0, 0)
            });
        }

        EventGrid.ItemsSource = events
            .OrderByDescending(e => e.CreatedAt)
            .Take(400)
            .Select(e => new
            {
                CreatedAt = e.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                e.NormalizedModel,
                e.RawModel,
                ReasoningEffort = ReasoningNormalizer.ToDisplay(e.ReasoningEffort),
                e.QuotaFamily,
                e.Source
            })
            .ToList();

        DrawTrend(trend);
    }

    private void DrawTrend(IReadOnlyList<DailyTrendPoint> trend)
    {
        ChartCanvas.Children.Clear();
        if (trend.Count == 0)
        {
            return;
        }

        ChartCanvas.UpdateLayout();
        var width = Math.Max(40, ChartCanvas.ActualWidth);
        var height = Math.Max(80, ChartCanvas.ActualHeight);
        var maxPro = Math.Max(1, trend.Max(p => p.ProCount));
        var maxReasoning = Math.Max(1, trend.Max(p => p.ReasoningCount));
        var step = width / Math.Max(1, trend.Count);
        var barWidth = Math.Max(3, (step - 8) / 2);
        for (var i = 0; i < trend.Count; i++)
        {
            var point = trend[i];
            var proHeight = height * (point.ProCount / (double)maxPro);
            var reasonHeight = height * (point.ReasoningCount / (double)maxReasoning);
            var pro = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth,
                Height = Math.Max(point.ProCount == 0 ? 0 : 1, proHeight),
                Fill = (System.Windows.Media.Brush)FindResource("AccentBrush")
            };
            Canvas.SetLeft(pro, i * step + 2);
            Canvas.SetTop(pro, height - proHeight);
            ChartCanvas.Children.Add(pro);

            var reason = new System.Windows.Shapes.Rectangle
            {
                Width = barWidth,
                Height = Math.Max(point.ReasoningCount == 0 ? 0 : 1, reasonHeight),
                Fill = new SolidColorBrush(Color.FromRgb(251, 191, 36))
            };
            Canvas.SetLeft(reason, i * step + 2 + barWidth + 2);
            Canvas.SetTop(reason, height - reasonHeight);
            ChartCanvas.Children.Add(reason);
        }
    }

    private void OnSync(object sender, RoutedEventArgs e) => SyncRequested?.Invoke();
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
}
