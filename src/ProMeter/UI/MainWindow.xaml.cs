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
        Headline.Text = $"GPT Pro usage: {snapshot.Used} / {snapshot.Limit}";
        PeriodText.Text = $"Current period {snapshot.PeriodStart.ToLocalTime():MMM d} – {snapshot.PeriodEnd.ToLocalTime():MMM d}   ·   Today {snapshot.TodayPro}";
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
        var max = Math.Max(1, trend.Max(p => p.ProCount + p.ReasoningCount));
        var step = width / Math.Max(1, trend.Count);
        for (var i = 0; i < trend.Count; i++)
        {
            var point = trend[i];
            var barHeight = height * (point.ProCount / (double)max);
            var bar = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(4, step - 6),
                Height = Math.Max(1, barHeight),
                Fill = (System.Windows.Media.Brush)FindResource("AccentBrush")
            };
            Canvas.SetLeft(bar, i * step + 3);
            Canvas.SetTop(bar, height - barHeight);
            ChartCanvas.Children.Add(bar);
        }
    }

    private void OnSync(object sender, RoutedEventArgs e) => SyncRequested?.Invoke();
    private void OnSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
}
