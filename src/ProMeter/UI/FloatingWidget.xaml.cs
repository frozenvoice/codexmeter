using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using ProMeter.Codex;

namespace ProMeter.UI;

public partial class FloatingWidget : Window
{
    public event Action<double, double>? Moved;
    public event Action? FlyoutRequested;
    public event Action? RefreshRequested;
    public event Action? ContextMenuRequested;

    private System.Windows.Point _dragStart;
    private bool _dragging;
    private bool _leftDown;

    public FloatingWidget()
    {
        InitializeComponent();
    }

    public void Bind(CodexQuotaSnapshot snapshot)
    {
        CodexLabel.Text = CodexDisplayFormatting.CompactWindowKindLabel(snapshot.CompactWindow);
        CodexValue.Text = CodexMeterPresentation.CompactText(snapshot).Replace("Codex ", "", StringComparison.Ordinal);
        HistoryValue.Text = CodexMeterPresentation.StatusLabel(snapshot);
        ToolTip = CodexMeterPresentation.Tooltip(snapshot);
    }

    public void Apply(AppSettings settings)
    {
        Left = settings.WidgetLeft;
        Top = settings.WidgetTop;
        Opacity = settings.WidgetOpacity;
        Topmost = settings.WidgetAlwaysOnTop;
        SetClickThrough(settings.WidgetClickThrough);
    }

    private void OnPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _leftDown = true;
        _dragging = false;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_leftDown || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (!_dragging && WidgetInteraction.IsDrag(_dragStart.X, _dragStart.Y, current.X, current.Y))
        {
            _dragging = true;
            DragMove();
            Moved?.Invoke(Left, Top);
        }
    }

    private void OnPreviewLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (!_leftDown)
        {
            return;
        }

        _leftDown = false;
        ReleaseMouseCapture();
        if (!_dragging)
        {
            FlyoutRequested?.Invoke();
        }
        else
        {
            Moved?.Invoke(Left, Top);
        }

        _dragging = false;
        e.Handled = true;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            RefreshRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Right)
        {
            ContextMenuRequested?.Invoke();
            e.Handled = true;
        }
    }

    private void SetClickThrough(bool enabled)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var style = GetWindowLong(hwnd, GwlExstyle);
        style = enabled ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLong(hwnd, GwlExstyle, style);
    }

    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x00000020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
