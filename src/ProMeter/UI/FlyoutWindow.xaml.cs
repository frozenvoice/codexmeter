using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ProMeter.Codex;

namespace ProMeter.UI;

public partial class FlyoutWindow : Window
{
    public event Action? SyncRequested;
    public event Action? SettingsRequested;
    public event Action<bool>? PinChanged;
    public event Action<double, double>? PositionChanged;
    public bool Pinned { get; private set; }
    private readonly RefreshIndicatorController _refreshIndicator = new();
    private bool _refreshActive;

    public FlyoutWindow()
    {
        InitializeComponent();
        ApplyLocalizedTexts();
        IsVisibleChanged += (_, _) => ApplyRefreshVisuals();
        Activated += (_, _) => ApplyRefreshVisuals();
        ContentRendered += (_, _) => ApplyRefreshVisuals();
        Closed += (_, _) =>
        {
            _refreshActive = false;
            ApplyRefreshVisuals();
        };
    }

    public void ApplyWindowSettings(AppSettings settings)
    {
        Pinned = settings.FlyoutPinned;
        Topmost = FlyoutWindowState.IsTopmost(Pinned);
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

    public void Bind(CodexQuotaSnapshot snapshot, bool refreshing = false)
    {
        ApplyLocalizedTexts();
        StatusText.Text = CodexMeterPresentation.StatusLabel(snapshot);
        BindCodex(snapshot);
        SetRefreshPresentation(new FlyoutRefreshPresentation(!refreshing, refreshing,
            refreshing ? UiText.CodexRefreshing : ""));
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
        SetRefreshing(presentation.Active);
    }

    public RefreshIndicatorController RefreshIndicator => _refreshIndicator;

    private void SetRefreshing(bool refreshing)
    {
        _refreshActive = refreshing;
        ApplyRefreshVisuals();
    }

    private void ApplyRefreshVisuals()
    {
        var state = FlyoutRefreshVisualState.Create(_refreshActive, IsVisible);
        RefreshAllIcon.Visibility = state.IdleIconVisible ? Visibility.Visible : Visibility.Collapsed;
        RefreshSpinner.Visibility = state.SpinnerVisible ? Visibility.Visible : Visibility.Collapsed;
        SyncProgressStrip.Visibility = state.ProgressStripVisible ? Visibility.Visible : Visibility.Collapsed;
        if (state.RunAnimation)
        {
            if (_refreshIndicator.Apply(true) == RefreshIndicatorTransition.Started)
            {
                StartRefreshAnimations();
            }
        }
        else if (_refreshIndicator.Reset() == RefreshIndicatorTransition.Stopped)
        {
            StopRefreshAnimations();
        }
    }

    private void StartRefreshAnimations()
    {
        var spinner = LiveSpinnerRotate();
        var spin = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(RefreshIndicatorController.DurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        spinner.BeginAnimation(RotateTransform.AngleProperty, spin, HandoffBehavior.SnapshotAndReplace);

        var strip = LiveProgressTranslate();
        var slide = new DoubleAnimation
        {
            From = -80,
            To = 328,
            Duration = TimeSpan.FromSeconds(RefreshIndicatorController.StripDurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        strip.BeginAnimation(TranslateTransform.XProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopRefreshAnimations()
    {
        var spinner = LiveSpinnerRotate();
        spinner.BeginAnimation(RotateTransform.AngleProperty, null);
        spinner.Angle = 0;

        var strip = LiveProgressTranslate();
        strip.BeginAnimation(TranslateTransform.XProperty, null);
        strip.X = -80;
    }

    private RotateTransform LiveSpinnerRotate()
    {
        if (RefreshSpinner.RenderTransform is RotateTransform current && !current.IsFrozen)
        {
            return current;
        }

        var live = RefreshSpinnerRotate.IsFrozen
            ? (RotateTransform)RefreshSpinnerRotate.Clone()
            : RefreshSpinnerRotate;
        RefreshSpinner.RenderTransform = live;
        RefreshSpinner.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        return live;
    }

    private TranslateTransform LiveProgressTranslate()
    {
        if (SyncProgressSegment.RenderTransform is TranslateTransform current && !current.IsFrozen)
        {
            return current;
        }

        var live = SyncProgressTranslate.IsFrozen
            ? (TranslateTransform)SyncProgressTranslate.Clone()
            : SyncProgressTranslate;
        SyncProgressSegment.RenderTransform = live;
        return live;
    }

    public void ApplyLocalizedTexts()
    {
        Title = UiText.ProductName;
        CodexSectionTitle.Text = CodexDisplayFormatting.SectionTitle;
        SettingsButton.ToolTip = UiText.Settings;
        System.Windows.Automation.AutomationProperties.SetName(SettingsButton, UiText.Settings);
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

    private const double CodexRingDiameter = 92;
    private const double CodexRingStrokeThickness = 9;
    private const double CodexRingRadius = (CodexRingDiameter - CodexRingStrokeThickness) / 2;
    private const double CodexRingCenter = CodexRingDiameter / 2;

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

        ApplyCodexRing(snapshot);
    }

    private void ApplyCodexRing(CodexQuotaSnapshot snapshot)
    {
        var ring = CodexRingPresentation.From(snapshot);
        CodexRingValueText.Text = ring.CenterValueText;
        CodexRingSubLabel.Text = ring.CenterSubLabel;

        var arcColor = (Brush)FindResource(ring.IsDangerLevel ? "DangerBrush" : "AccentBrush");
        CodexRingArcPath.Stroke = arcColor;
        CodexRingFullCircle.Stroke = arcColor;
        CodexRingTrack.Stroke = (Brush)FindResource(ring.IsAvailable ? "LineBrush" : "DisabledBrush");

        var arc = RingGeometry.ComputeUsedArc(ring.UsedPercent, CodexRingCenter, CodexRingCenter, CodexRingRadius);
        CodexRingArcPath.Visibility = arc.Visible ? Visibility.Visible : Visibility.Collapsed;
        CodexRingFullCircle.Visibility = arc.IsFullCircle ? Visibility.Visible : Visibility.Collapsed;
        if (arc.Visible)
        {
            CodexRingFigure.StartPoint = new System.Windows.Point(arc.Start.X, arc.Start.Y);
            CodexRingArcSegment.Point = new System.Windows.Point(arc.End.X, arc.End.Y);
            CodexRingArcSegment.Size = new System.Windows.Size(CodexRingRadius, CodexRingRadius);
            CodexRingArcSegment.IsLargeArc = arc.IsLargeArc;
        }

        CodexLegendPanel.Visibility = ring.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (ring.IsAvailable)
        {
            var usedPercent = ring.UsedPercent!.Value;
            CodexLegendUsedText.Text = $"{UiText.CodexLegendUsed} {ring.CenterValueText}";
            CodexLegendRemainingText.Text = $"{UiText.CodexLegendRemaining} {CodexDisplayFormatting.PercentText(Math.Clamp(100 - usedPercent, 0, 100))}";
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

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Pinned = !Pinned;
        Topmost = FlyoutWindowState.IsTopmost(Pinned);
        ApplyPinGlyph();
        PinChanged?.Invoke(Pinned);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Hide();

    private void OnHeaderMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!FlyoutWindowState.AllowsHeaderDrag || e.ChangedButton != MouseButton.Left)
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

}
