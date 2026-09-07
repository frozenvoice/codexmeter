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

    private WidgetDragSession? _drag;
    private System.Windows.Media.Matrix _dragFromDevice;

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
        Cursor = settings.WidgetClickThrough ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll;
    }

    private System.Windows.Point PointerOnScreen(System.Windows.Input.MouseEventArgs e) =>
        _dragFromDevice.Transform(PointToScreen(e.GetPosition(this)));

    private void OnPreviewLeftDown(object sender, MouseButtonEventArgs e)
    {
        _dragFromDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice
            ?? System.Windows.Media.Matrix.Identity;
        var pointer = PointerOnScreen(e);
        BeginDrag(pointer);
        e.Handled = true;
    }

    private void OnPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_drag is null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { FinishDrag(false); return; }
        var pointer = PointerOnScreen(e);
        UpdateDragPosition(pointer);
        e.Handled = true;
    }

    private void OnPreviewLeftUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag is null) return;
        var pointer = PointerOnScreen(e);
        UpdateDragPosition(pointer);
        FinishDrag(true);
        e.Handled = true;
    }

    private void OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => FinishDrag(false);

    private void BeginDrag(System.Windows.Point pointer)
    {
        _drag = new WidgetDragSession(Left, Top, pointer.X, pointer.Y);
        if (!CaptureMouse()) _drag = null;
    }

    private void UpdateDragPosition(System.Windows.Point pointer)
    {
        if (_drag is null) return;
        var position = _drag.Move(pointer.X, pointer.Y);
        if (_drag.IsDragging) { Left = position.Left; Top = position.Top; }
    }

    private void FinishDrag(bool allowClick)
    {
        var gesture = _drag;
        _drag = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (gesture?.IsDragging == true) Moved?.Invoke(Left, Top);
        else if (gesture is not null && allowClick) FlyoutRequested?.Invoke();
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
