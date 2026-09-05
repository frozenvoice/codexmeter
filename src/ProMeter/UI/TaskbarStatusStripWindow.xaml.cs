using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using ProMeter.Codex;

namespace ProMeter.UI;

public partial class TaskbarStatusStripWindow : Window
{
    public event Action? FlyoutRequested;
    public event Action? RefreshRequested;
    public event Action? ContextMenuRequested;

    private readonly LayoutSignalDebouncer _debounce = new();
    private readonly DispatcherTimer _layoutTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _fullscreenTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private HwndSource? _hwnd;
    private QuotaSnapshot _chatgpt = new();
    private CodexQuotaSnapshot _codex = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
    private TaskbarLayoutResult _layout;
    public TaskbarEdge LastEdge { get; private set; } = TaskbarEdge.Bottom;
    public Rect LastBounds => new(Left, Top, Width, Height);

    public TaskbarStatusStripWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonUp += (_, _) => FlyoutRequested?.Invoke();
        MouseDown += OnMouseDown;
        MouseEnter += (_, _) => Chrome.Background = (Brush)FindResource("GhostBrush");
        MouseLeave += (_, _) => Chrome.Background = (Brush)FindResource("CardBrush");
        _layoutTimer.Tick += (_, _) =>
        {
            _layoutTimer.Stop();
            Reposition();
        };
        _fullscreenTimer.Tick += (_, _) => Reposition();
        SystemEvents.DisplaySettingsChanged += OnSystemLayout;
        SystemEvents.SessionSwitch += OnSystemLayout;
        SystemEvents.UserPreferenceChanged += OnSystemLayout;
        UiText.Changed += OnLanguageChanged;
    }

    public void Bind(QuotaSnapshot chatgpt, CodexQuotaSnapshot codex)
    {
        _chatgpt = chatgpt;
        _codex = codex;
        ApplyText();
        Reposition();
    }

    public void ApplyThemeResources() => ApplyText();

    private void ApplyText()
    {
        var mode = _layout.Visible ? _layout.Mode : TaskbarStripMode.Full;
        if (mode is TaskbarStripMode.AboveTaskbar or TaskbarStripMode.Hidden)
        {
            mode = TaskbarStripMode.UltraCompact;
        }

        Label.Text = TaskbarStatusFormatter.Format(_chatgpt, _codex, mode);
        Label.Foreground = _codex.CompactWindow?.UsedPercent >= 100
            ? (Brush)FindResource("DangerBrush")
            : (Brush)FindResource("TextBrush");
        ToolTip = TaskbarStatusFormatter.Tooltip(_chatgpt, _codex);
    }

    public void Reposition()
    {
        try
        {
            var hwnd = _hwnd?.Handle ?? IntPtr.Zero;
            var input = TaskbarWin32.Capture(hwnd);
            LastEdge = input.Edge;
            _layout = TaskbarStatusPositioner.Place(input);
            if (!_layout.Visible)
            {
                Hide();
                return;
            }

            var dip = TaskbarStatusPositioner.ToDip(_layout.Bounds, input.DpiScale);
            Left = dip.Left;
            Top = dip.Top;
            Width = Math.Max(1, dip.Width);
            Height = Math.Max(1, dip.Height);
            Topmost = input.TaskbarVisible && !input.ExclusiveFullscreenOnMonitor;
            ApplyText();
            if (!IsVisible)
            {
                Show();
            }
        }
        catch
        {
            // Best-effort overlay; the tray icon stays available.
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = (HwndSource)PresentationSource.FromVisual(this)!;
        TaskbarWin32.ApplyToolWindowStyles(_hwnd.Handle);
        _hwnd.AddHook(Hook);
        _fullscreenTimer.Start();
        Reposition();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarWin32.TaskbarCreatedMessage
            || msg is TaskbarWin32.WmDisplayChange
                or TaskbarWin32.WmDpiChanged
                or TaskbarWin32.WmSettingChange
                or TaskbarWin32.WmThemeChanged
                or TaskbarWin32.WmWtsSessionChange)
        {
            RequestReposition();
        }

        return IntPtr.Zero;
    }

    private void RequestReposition()
    {
        if (_debounce.ShouldHandle(DateTimeOffset.Now, TimeSpan.FromMilliseconds(200)))
        {
            Reposition();
            return;
        }

        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            RefreshRequested?.Invoke();
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            ContextMenuRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void OnSystemLayout(object? sender, EventArgs e) => RequestReposition();

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(ApplyText);

    protected override void OnClosed(EventArgs e)
    {
        _fullscreenTimer.Stop();
        _layoutTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnSystemLayout;
        SystemEvents.SessionSwitch -= OnSystemLayout;
        SystemEvents.UserPreferenceChanged -= OnSystemLayout;
        UiText.Changed -= OnLanguageChanged;
        _hwnd?.RemoveHook(Hook);
        base.OnClosed(e);
    }
}
