using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ProMeter.Codex;

namespace ProMeter.UI;

public partial class FlyoutWindow : Window
{
    public event Action? CoverageRequested;
    public event Action? SyncRequested;
    public event Action<bool>? PinChanged;
    public event Action<double, double>? PositionChanged;
    public bool CloseOnDeactivate { get; set; } = true;
    public bool Pinned { get; private set; }
    private bool _closeOnDeactivateSetting = true;
    private readonly RefreshIndicatorController _refreshIndicator = new();
    private Storyboard? _refreshStoryboard;
    private Storyboard? _progressStripStoryboard;
    private bool _refreshActive;

    public FlyoutWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();
        IsVisibleChanged += (_, _) => ApplyRefreshIndicator(IsVisible && _refreshActive);
        Closed += (_, _) =>
        {
            _refreshActive = false;
            ApplyRefreshIndicator(false);
        };
    }

    public void ApplyWindowSettings(AppSettings settings)
    {
        _closeOnDeactivateSetting = settings.FlyoutCloseOnDeactivate;
        Pinned = settings.FlyoutPinned;
        CloseOnDeactivate = FlyoutWindowState.ShouldCloseOnDeactivate(Pinned, _closeOnDeactivateSetting);
        Topmost = true;
        ApplyPinGlyph();
    }

    public void RestorePosition(double left, double top)
    {
        UpdateLayout();
        var height = Math.Max(ActualHeight, 1);
        var work = FlyoutPlacement.SelectWorkArea(left, top, Width, height, EnumerateWorkAreas());
        var clamped = FlyoutPlacement.ClampToWorkArea(left, top, Width, height, work);
        Left = clamped.Left;
        Top = clamped.Top;
    }

    public void Bind(
        QuotaSnapshot snapshot,
        AppSettings settings,
        CodexQuotaSnapshot? codex = null,
        bool chatGptRefreshing = false,
        bool codexRefreshing = false,
        bool combinedManual = false)
    {
        ApplyLocalizedTexts();
        var presentation = ProStatusPresentation.From(snapshot);
        StatusText.Text = DisplayFormatting.StatusLabel(snapshot);
        ProStateText.Text = presentation.ProStateText;
        ServerResetText.Text = presentation.ResetText;
        ExactRemainingText.Text = presentation.ExactRemainingText;
        RestrictionDetailText.Text = presentation.RestrictionDetail;
        RestrictionDetailText.Visibility = string.IsNullOrWhiteSpace(presentation.RestrictionDetail)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ConfirmedRequestsText.Text = presentation.ConfirmedRequestsText;
        CountSourceText.Text = presentation.CountSourceText;
        AuthoritativeCountPanel.Visibility = presentation.ExactRemainingAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (presentation.ExactRemainingAvailable)
        {
            ProCountText.Text = $"{snapshot.Used} / {snapshot.Limit}";
        }

        var showPro200 = snapshot.SolProDailyLimit is not null || snapshot.CombinedDailyLimit is not null;
        Pro200Panel.Visibility = showPro200 ? Visibility.Visible : Visibility.Collapsed;
        if (showPro200)
        {
            Gpt6WeekText.Text = DisplayFormatting.WindowUsage(
                snapshot.UsesServerWeeklyCount ? snapshot.Used : snapshot.Gpt6WeeklyUsed,
                snapshot.Limit,
                snapshot.UsesServerWeeklyCount);
            SolDailyText.Text = snapshot.SolProDailyLimit is int sol
                ? DisplayFormatting.WindowUsage(snapshot.TodaySolPro, sol, snapshot.UsesServerSolDailyCount)
                : "—";
            CombinedDailyText.Text = snapshot.CombinedDailyLimit is int combined
                ? DisplayFormatting.WindowUsage(snapshot.CombinedToday, combined, snapshot.UsesServerCombinedDailyCount)
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
        SetRefreshPresentation(CombinedRefreshCoordinator.Present(chatGptRefreshing, codexRefreshing, combinedManual));

        Dispatcher.BeginInvoke(() =>
        {
            if (!presentation.ExactRemainingAvailable)
            {
                ProBar.Width = 0;
                return;
            }

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
        RefreshProgressText.Text = presentation.ProgressText;
        StatusText.Visibility = presentation.ShowNormalStatus ? Visibility.Visible : Visibility.Collapsed;
        RefreshProgressText.Visibility = presentation.ShowRefreshProgress ? Visibility.Visible : Visibility.Collapsed;
        _refreshActive = presentation.Active;
        ApplyRefreshIndicator(presentation.Active);
    }

    public RefreshIndicatorController RefreshIndicator => _refreshIndicator;

    private void ApplyRefreshIndicator(bool active)
    {
        var visibleActive = active && IsVisible;
        RefreshAllIcon.Visibility = visibleActive ? Visibility.Collapsed : Visibility.Visible;
        RefreshSpinner.Visibility = visibleActive ? Visibility.Visible : Visibility.Collapsed;
        SyncProgressStrip.Visibility = visibleActive ? Visibility.Visible : Visibility.Collapsed;
        var transition = visibleActive
            ? _refreshIndicator.Apply(true)
            : _refreshIndicator.Reset();
        if (transition == RefreshIndicatorTransition.Started)
        {
            EnsureRefreshStoryboard().Begin(this, true);
            EnsureProgressStripStoryboard().Begin(this, true);
        }
        else if (transition == RefreshIndicatorTransition.Stopped)
        {
            _refreshStoryboard?.Stop(this);
            _progressStripStoryboard?.Stop(this);
            RefreshSpinnerRotate.Angle = 0;
            SyncProgressTranslate.X = -80;
        }
    }

    private Storyboard EnsureRefreshStoryboard()
    {
        if (_refreshStoryboard is not null)
        {
            return _refreshStoryboard;
        }

        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(RefreshIndicatorController.DurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, RefreshSpinnerRotate);
        Storyboard.SetTargetProperty(animation, new PropertyPath(RotateTransform.AngleProperty));
        _refreshStoryboard = new Storyboard();
        _refreshStoryboard.Children.Add(animation);
        return _refreshStoryboard;
    }

    private Storyboard EnsureProgressStripStoryboard()
    {
        if (_progressStripStoryboard is not null)
        {
            return _progressStripStoryboard;
        }

        var animation = new DoubleAnimation
        {
            From = -80,
            To = 328,
            Duration = TimeSpan.FromSeconds(RefreshIndicatorController.StripDurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(animation, SyncProgressTranslate);
        Storyboard.SetTargetProperty(animation, new PropertyPath(TranslateTransform.XProperty));
        _progressStripStoryboard = new Storyboard();
        _progressStripStoryboard.Children.Add(animation);
        return _progressStripStoryboard;
    }

    public void ApplyLocalizedTexts()
    {
        ProStateLabel.Text = UiText.T("Status", "상태");
        ServerResetLabel.Text = UiText.ServerReset;
        ExactRemainingLabel.Text = UiText.ExactRemaining;
        HistoryStatsLabel.Text = UiText.HistoryStatistics;
        ConfirmedRequestsLabel.Text = UiText.ConfirmedProRequests;
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
        ApplyPinGlyph();
        CloseFlyoutButton.ToolTip = UiText.Close;
        System.Windows.Automation.AutomationProperties.SetName(CloseFlyoutButton, UiText.Close);
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

    private void OnRefreshAllClick(object sender, RoutedEventArgs e) => SyncRequested?.Invoke();

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Pinned = !Pinned;
        CloseOnDeactivate = FlyoutWindowState.ShouldCloseOnDeactivate(Pinned, _closeOnDeactivateSetting);
        ApplyPinGlyph();
        PinChanged?.Invoke(Pinned);
        if (Pinned)
        {
            PersistPosition();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!Pinned || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (HeaderSourceIsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }

        PersistPosition();
    }

    private void PersistPosition()
    {
        RestorePosition(Left, Top);
        PositionChanged?.Invoke(Left, Top);
    }

    private void ApplyPinGlyph()
    {
        PinFilled.Visibility = Pinned ? Visibility.Visible : Visibility.Collapsed;
        PinOutline.Visibility = Pinned ? Visibility.Collapsed : Visibility.Visible;
        var label = Pinned ? UiText.Unpin : UiText.Pin;
        PinButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(PinButton, label);
    }

    private bool HeaderSourceIsInteractive(DependencyObject? source)
    {
        while (source is not null && !ReferenceEquals(source, FlyoutHeaderGrid))
        {
            if (source is System.Windows.Controls.Button
                || ReferenceEquals(source, StatusText)
                || ReferenceEquals(source, RefreshProgressText)
                || ReferenceEquals(source, RefreshAllButton)
                || ReferenceEquals(source, PinButton)
                || ReferenceEquals(source, CloseFlyoutButton))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private IReadOnlyList<ScreenRect> EnumerateWorkAreas()
    {
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var areas = new List<ScreenRect>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var area = screen.WorkingArea;
            var topLeft = fromDevice.Transform(new System.Windows.Point(area.Left, area.Top));
            var bottomRight = fromDevice.Transform(new System.Windows.Point(area.Right, area.Bottom));
            areas.Add(new ScreenRect(
                (int)topLeft.X,
                (int)topLeft.Y,
                (int)Math.Max(1, bottomRight.X - topLeft.X),
                (int)Math.Max(1, bottomRight.Y - topLeft.Y)));
        }

        return areas;
    }

    private void OnCoverageClick(object sender, RoutedEventArgs e) => CoverageRequested?.Invoke();
}
