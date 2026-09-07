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
        SourceInitialized += (_, _) => FitContentToWorkArea();
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
        StatusText.Text = snapshot.Status == CodexQuotaStatus.Available ? UiText.T("Up to date", "정상 작동 중") : CodexMeterPresentation.StatusLabel(snapshot);
        StatusDot.Fill = (Brush)FindResource(refreshing ? "AccentBrush" : snapshot.Status == CodexQuotaStatus.Available ? "OkBrush" : "MutedBrush");
        BindCodex(snapshot);
        BindCreditCard(snapshot);
        SetRefreshPresentation(new FlyoutRefreshPresentation(!refreshing, refreshing,
            refreshing ? UiText.CodexRefreshing : ""));
    }

    public void SetRefreshPresentation(FlyoutRefreshPresentation presentation)
    {
        RefreshAllButton.IsEnabled = presentation.Enabled;
        RefreshAllIcon.Stroke = presentation.Active
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
    }

    private void StopRefreshAnimations()
    {
        var spinner = LiveSpinnerRotate();
        spinner.BeginAnimation(RotateTransform.AngleProperty, null);
        spinner.Angle = 0;
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

    public void ApplyLocalizedTexts()
    {
        Title = UiText.ProductName;
        ResetCreditsTitle.Text = UiText.ResetCredits;
        CreditHelpButton.ToolTip = MakeTooltip(UiText.T("Reset credits can renew your Codex usage limits. This app only shows their availability and expiry dates.", "리셋권으로 Codex 사용 한도를 갱신할 수 있습니다. 이 앱에서는 보유 수와 만료일만 확인합니다."));
        System.Windows.Automation.AutomationProperties.SetName(CreditHelpButton, UiText.T("About reset credits", "리셋권 안내"));
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

    private const double CodexRingDiameter = 168;
    private const double CodexRingStrokeThickness = 15;
    private const double CodexRingRadius = (CodexRingDiameter - CodexRingStrokeThickness) / 2;
    private const double CodexRingCenter = CodexRingDiameter / 2;

    private void BindCodex(CodexQuotaSnapshot snapshot)
    {
        CodexStatusText.Text = snapshot.Status == CodexQuotaStatus.Refreshing ? "" : CodexDisplayFormatting.StatusText(snapshot);
        CodexStatusText.Visibility = string.IsNullOrWhiteSpace(CodexStatusText.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        CodexRows.Items.Clear();
        foreach (var item in CodexDisplayFormatting.Rows(snapshot, includeResetCredits: false))
        {
            var row = new Grid { Margin = new Thickness(0, 13, 0, 13), MinHeight = 20 };
            if (item.Tooltip is not null)
            {
                row.ToolTip = new System.Windows.Controls.ToolTip
                {
                    Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("LineBrush"),
                    Content = new TextBlock { Text = item.Tooltip, Foreground = (Brush)FindResource("TextBrush") }
                };
            }
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new TextBlock
            {
                Text = item.Label,
                Margin = new Thickness(0, 0, 12, 0), FontSize = 15, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush")
            });
            var values = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
            Grid.SetColumn(values, 1);
            values.Children.Add(new TextBlock
            {
                Text = item.Value, FontSize = 17,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Style = (Style)FindResource("FlyoutValueText"),
                Foreground = item.EmphasizeDanger ? (Brush)FindResource("DangerBrush") : (Brush)FindResource("TextBrush")
            });
            if (!string.IsNullOrWhiteSpace(item.Detail))
            {
                values.Children.Add(new TextBlock
                {
                    Text = item.Detail, FontSize = 13, Margin = new Thickness(0, 2, 0, 2),
                    TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right,
                    Foreground = (Brush)FindResource("MutedBrush")
                });
            }
            row.Children.Add(values);
            CodexRows.Items.Add(new Border
            {
                Child = row, BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = CodexRows.Items.Count == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0)
            });
        }

        ApplyCodexRing(snapshot);
    }

    private void BindCreditCard(CodexQuotaSnapshot snapshot)
    {
        var credits = CodexCreditCard.From(snapshot, DateTimeOffset.Now);
        ResetCreditsCount.Text = credits.CountText;
        CreditExpiryRows.Items.Clear();
        for (var index = 0; index < credits.Rows.Count; index++)
        {
            var item = credits.Rows[index];
            var row = new Grid { MinHeight = 50 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M3,5 L19,5 L19,20 L3,20 Z M3,9 L19,9 M7,2 L7,6 M15,2 L15,6"),
                Width = 19, Height = 20, Stretch = Stretch.Uniform,
                Stroke = (Brush)FindResource("MutedBrush"), StrokeThickness = 1.5,
                VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left
            });
            var text = new TextBlock
            {
                Text = item.Text, FontSize = 15, Margin = new Thickness(10, 10, 4, 10),
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("MutedBrush")
            };
            Grid.SetColumn(text, 1); row.Children.Add(text);
            row.ToolTip = MakeTooltip(item.Tooltip);
            CreditExpiryRows.Items.Add(new Border
            {
                Child = row, BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = index + 1 < credits.Rows.Count ? new Thickness(0, 0, 0, 1) : new Thickness(0)
            });
        }
        CreditListBorder.Visibility = credits.Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CreditExpiryNotice.Text = credits.Notice;
        CreditExpiryNotice.Visibility = credits.Notice is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private System.Windows.Controls.ToolTip MakeTooltip(string text) => new()
    {
        Background = (Brush)FindResource("CardBrush"), BorderBrush = (Brush)FindResource("LineBrush"),
        Content = new TextBlock { Text = text, MaxWidth = 320, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextBrush") }
    };

    private void OnCreditHelpClick(object sender, RoutedEventArgs e)
    {
        if (CreditHelpButton.ToolTip is System.Windows.Controls.ToolTip tip)
        {
            tip.PlacementTarget = CreditHelpButton;
            tip.IsOpen = true;
        }
    }

    private void FitContentToWorkArea()
    {
        var source = PresentationSource.FromVisual(this);
        var fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var cursor = System.Windows.Forms.Control.MousePosition;
        var work = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;
        var size = fromDevice.Transform(new System.Windows.Vector(work.Width, work.Height));
        Width = Math.Min(560, Math.Max(360, size.X - 24));
        FlyoutContentScroll.MaxHeight = Math.Max(180, size.Y - 128);
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
