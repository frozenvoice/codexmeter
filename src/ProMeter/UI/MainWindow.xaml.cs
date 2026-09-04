using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ProMeter.UI;

public partial class MainWindow : Window
{
    public event Action? SyncRequested;
    public event Action? SettingsRequested;

    public MainWindow()
    {
        InitializeComponent();
    }

    public void Bind(QuotaSnapshot snapshot, IReadOnlyList<UsageEvent> events, IReadOnlyList<DailyTrendPoint> trend)
    {
        Headline.Text = DisplayFormatting.Headline(snapshot);
        PeriodText.Text = snapshot.SolProDailyLimit is int sol && snapshot.CombinedDailyLimit is int combined
            ? $"Current period {snapshot.PeriodStart.ToLocalTime():MMM d} – {snapshot.PeriodEnd.ToLocalTime():MMM d}   ·   GPT-6 week {snapshot.Gpt6WeeklyUsed}   ·   Sol daily {snapshot.TodaySolPro}/{sol}   ·   Combined daily {snapshot.CombinedToday}/{combined}"
            : $"Current period {snapshot.PeriodStart.ToLocalTime():MMM d} – {snapshot.PeriodEnd.ToLocalTime():MMM d}   ·   Today {snapshot.TodayPro}";
        StatusText.Text = $"{DisplayFormatting.StatusLabel(snapshot.Status)}   ·   Reset {DisplayFormatting.ResetLabel(snapshot)}   ·   Coverage {snapshot.Coverage.SummaryLabel}";
        ReasonText.Text = $"Today {snapshot.Reasoning.Today}   This week {snapshot.Reasoning.ThisWeek}   Medium {snapshot.Reasoning.Medium}   High {snapshot.Reasoning.High}   Extra High {snapshot.Reasoning.ExtraHigh}   Limit {(snapshot.Reasoning.Limit?.ToString() ?? "Unknown")}";
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
