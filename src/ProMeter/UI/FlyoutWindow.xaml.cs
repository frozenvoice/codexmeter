using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ProMeter.Codex;

namespace ProMeter.UI;

public partial class FlyoutWindow : Window
{
    public event Action? CoverageRequested;
    public event Action? SyncRequested;
    public bool CloseOnDeactivate { get; set; } = true;
    private bool _suppressDeactivateClose;

    public FlyoutWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();
    }

    public void Bind(
        QuotaSnapshot snapshot,
        AppSettings settings,
        CodexQuotaSnapshot? codex = null,
        bool chatGptRefreshing = false,
        bool codexRefreshing = false)
    {
        ApplyLocalizedTexts();
        StatusText.Text = DisplayFormatting.StatusLabel(snapshot);
        ProCountText.Text = DisplayFormatting.UsageLabel(snapshot);
        RemainingText.Text = snapshot.DisplayUsageUnavailable
            ? "?"
            : snapshot.Remaining.ToString(CultureInfo.InvariantCulture);
        CountSourceText.Text = DisplayFormatting.CountSourceLabel(snapshot);
        var showPro200 = snapshot.SolProDailyLimit is not null || snapshot.CombinedDailyLimit is not null;
        Pro200Panel.Visibility = showPro200 ? Visibility.Visible : Visibility.Collapsed;
        if (showPro200)
        {
            Gpt6WeekText.Text = $"{snapshot.Gpt6WeeklyUsed} / {snapshot.Limit}{UiText.RemainingWithCount(Math.Max(0, snapshot.Limit - snapshot.Gpt6WeeklyUsed).ToString(CultureInfo.InvariantCulture))}";
            SolDailyText.Text = snapshot.SolProDailyLimit is int sol
                ? $"{snapshot.TodaySolPro} / {sol}{UiText.RemainingWithCount(snapshot.SolProDailyRemaining.ToString(CultureInfo.InvariantCulture))}"
                : "—";
            CombinedDailyText.Text = snapshot.CombinedDailyLimit is int combined
                ? $"{snapshot.CombinedToday} / {combined}{UiText.RemainingWithCount(snapshot.CombinedDailyRemaining.ToString(CultureInfo.InvariantCulture))}"
                : "—";
        }

        var reset = DisplayFormatting.ResetDisplay(snapshot);
        ResetTimeLabel.Text = reset.TimeLabel;
        ResetText.Text = reset.TimeValue;
        if (reset.EstimateValue is null)
        {
            ResetEstimatePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ResetEstimatePanel.Visibility = Visibility.Visible;
            ResetEstimateLabel.Text = reset.EstimateLabel;
            ResetEstimateText.Text = reset.EstimateValue;
        }

        SyncText.Text = DisplayFormatting.LastSyncLabel(snapshot.LastSync);
        CoverageText.Text = DisplayFormatting.CoverageFlyoutValue(snapshot);
        ReasonToday.Text = snapshot.Reasoning.Today.ToString(CultureInfo.InvariantCulture);
        ReasonWeek.Text = snapshot.Reasoning.ThisWeek.ToString(CultureInfo.InvariantCulture);
        ReasonMedium.Text = snapshot.Reasoning.Medium.ToString(CultureInfo.InvariantCulture);
        ReasonHigh.Text = snapshot.Reasoning.High.ToString(CultureInfo.InvariantCulture);
        ReasonExtra.Text = snapshot.Reasoning.ExtraHigh.ToString(CultureInfo.InvariantCulture);
        if (snapshot.Reasoning.Limit is int limit)
        {
            ReasonLimitPanel.Visibility = Visibility.Visible;
            ReasonLimit.Text = limit.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            ReasonLimitPanel.Visibility = Visibility.Collapsed;
        }

        BindCodex(codex ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable));
        SetRefreshPresentation(CombinedRefreshCoordinator.Present(chatGptRefreshing, codexRefreshing));

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
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("FlyoutValueText")
            };
            DockPanel.SetDock(count, Dock.Right);
            row.Children.Add(count);
            ModelRows.Items.Add(row);
        }
    }

    public void SetRefreshPresentation(FlyoutRefreshPresentation presentation)
    {
        RefreshAllButton.IsEnabled = presentation.Enabled;
        RefreshAllIcon.Foreground = presentation.Active
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("TextBrush");
    }

    public void ApplyLocalizedTexts()
    {
        RemainingLabel.Text = UiText.Remaining;
        Gpt6WeekLabel.Text = UiText.Gpt6ProWeek;
        SolDailyLabel.Text = UiText.SolProDaily;
        CombinedDailyLabel.Text = UiText.CombinedDaily;
        CodexSectionTitle.Text = CodexDisplayFormatting.SectionTitle;
        ReasoningSectionTitle.Text = UiText.SolReasoning;
        ReasonTodayLabel.Text = UiText.Today;
        ReasonWeekLabel.Text = UiText.ThisWeek;
        ReasonMediumLabel.Text = UiText.Medium;
        ReasonHighLabel.Text = UiText.High;
        ReasonExtraLabel.Text = UiText.ExtraHigh;
        ReasonLimitLabel.Text = UiText.LimitInfo;
        ReasoningNoteText.Text = UiText.ReasoningReconstructedNote;
        StatusSectionTitle.Text = UiText.Status;
        LastSyncLabel.Text = UiText.LastSync;
        CoverageLabel.Text = UiText.DataStatus;
        RefreshAllButton.ToolTip = UiText.RefreshAll;
        System.Windows.Automation.AutomationProperties.SetName(RefreshAllButton, UiText.RefreshAll);
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

    public void PlaceNear(Rect anchor, TaskbarEdge edge)
    {
        UpdateLayout();
        var work = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point((int)anchor.X, (int)anchor.Y)).WorkingArea;
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var workTopLeft = fromDevice.Transform(new System.Windows.Point(work.Left, work.Top));
        var workBottomRight = fromDevice.Transform(new System.Windows.Point(work.Right, work.Bottom));
        var (left, top) = FlyoutPlacement.PlaceNear(
            new ScreenRect((int)anchor.X, (int)anchor.Y, (int)anchor.Width, (int)anchor.Height),
            edge,
            Width,
            ActualHeight,
            new ScreenRect(
                (int)workTopLeft.X,
                (int)workTopLeft.Y,
                (int)(workBottomRight.X - workTopLeft.X),
                (int)(workBottomRight.Y - workTopLeft.Y)));
        Left = left;
        Top = top;
    }

    private void BindCodex(CodexQuotaSnapshot snapshot)
    {
        CodexStatusText.Text = CodexDisplayFormatting.StatusText(snapshot);
        CodexStatusText.Visibility = string.IsNullOrWhiteSpace(CodexStatusText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        CodexRows.Items.Clear();
        foreach (var item in CodexDisplayFormatting.Rows(snapshot))
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock
            {
                Text = item.Label,
                Foreground = (Brush)FindResource("MutedBrush")
            });
            var value = new TextBlock
            {
                Text = item.Value,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("FlyoutValueText"),
                Foreground = item.EmphasizeDanger
                    ? (Brush)FindResource("DangerBrush")
                    : (Brush)FindResource("TextBrush")
            };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            CodexRows.Items.Add(row);
        }
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        if (_suppressDeactivateClose)
        {
            _suppressDeactivateClose = false;
            return;
        }

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

    private void OnRefreshPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _suppressDeactivateClose = true;
    }

    private void OnRefreshAllClick(object sender, RoutedEventArgs e)
    {
        _suppressDeactivateClose = true;
        SyncRequested?.Invoke();
    }

    private void OnCoverageClick(object sender, RoutedEventArgs e) => CoverageRequested?.Invoke();
}
