using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ProMeter.UI;

public partial class FlyoutWindow : Window
{
    public event Action? CoverageRequested;
    public bool CloseOnDeactivate { get; set; } = true;

    public FlyoutWindow()
    {
        InitializeComponent();
    }

    public void Bind(QuotaSnapshot snapshot, AppSettings settings)
    {
        StatusText.Text = DisplayFormatting.StatusLabel(snapshot.Status);
        ProCountText.Text = $"{snapshot.Used} / {snapshot.Limit}";
        RemainingText.Text = snapshot.Remaining.ToString(CultureInfo.InvariantCulture);
        CountSourceText.Text = snapshot.UsesServerCount
            ? $"Server count · reconstructed {snapshot.ReconstructedUsed}"
            : snapshot.Coverage.CountConfidence == CoverageConfidence.HighConfidence
                ? "Reconstructed · high confidence"
                : "Reconstructed · estimated";
        var showPro200 = snapshot.SolProDailyLimit is not null || snapshot.CombinedDailyLimit is not null;
        Pro200Panel.Visibility = showPro200 ? Visibility.Visible : Visibility.Collapsed;
        if (showPro200)
        {
            Gpt6WeekText.Text = $"{snapshot.Gpt6WeeklyUsed} / {snapshot.Limit}  remaining {Math.Max(0, snapshot.Limit - snapshot.Gpt6WeeklyUsed)}";
            SolDailyText.Text = snapshot.SolProDailyLimit is int sol
                ? $"{snapshot.TodaySolPro} / {sol}  remaining {snapshot.SolProDailyRemaining}"
                : "—";
            CombinedDailyText.Text = snapshot.CombinedDailyLimit is int combined
                ? $"{snapshot.CombinedToday} / {combined}  remaining {snapshot.CombinedDailyRemaining}"
                : "—";
        }

        ResetText.Text = DisplayFormatting.ResetLabel(snapshot);
        SyncText.Text = DisplayFormatting.LastSyncLabel(snapshot.LastSync);
        CoverageText.Text = $"{snapshot.Coverage.ApproximatePercent}% / {snapshot.Coverage.SummaryLabel}";
        ReasonToday.Text = snapshot.Reasoning.Today.ToString(CultureInfo.InvariantCulture);
        ReasonWeek.Text = snapshot.Reasoning.ThisWeek.ToString(CultureInfo.InvariantCulture);
        ReasonMedium.Text = snapshot.Reasoning.Medium.ToString(CultureInfo.InvariantCulture);
        ReasonHigh.Text = snapshot.Reasoning.High.ToString(CultureInfo.InvariantCulture);
        ReasonExtra.Text = snapshot.Reasoning.ExtraHigh.ToString(CultureInfo.InvariantCulture);
        ReasonLimit.Text = snapshot.Reasoning.Limit is int limit ? limit.ToString(CultureInfo.InvariantCulture) : "Unknown";

        Dispatcher.BeginInvoke(() =>
        {
            var width = Math.Max(8, (ProBar.Parent as FrameworkElement)?.ActualWidth * snapshot.PercentUsed ?? 0);
            ProBar.Width = width;
        });

        ModelRows.Items.Clear();
        foreach (var model in snapshot.ModelBreakdown)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = model.NormalizedModel, Foreground = (Brush)FindResource("MutedBrush") });
            var count = new TextBlock
            {
                Text = model.Count.ToString(CultureInfo.InvariantCulture),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            ModelRows.Items.Add(row);
        }
    }

    public void PlaceNearTaskbar()
    {
        UpdateLayout();
        var cursor = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor);
        var area = screen.WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = fromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
        var bottomRight = fromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
        Left = Math.Max(topLeft.X + 8, bottomRight.X - Width - 12);
        Top = Math.Max(topLeft.Y + 8, bottomRight.Y - ActualHeight - 12);
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (CloseOnDeactivate)
        {
            Hide();
        }
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
        }
    }

    private void OnCoverageClick(object sender, RoutedEventArgs e) => CoverageRequested?.Invoke();
}
