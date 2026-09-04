using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace ProMeter.UI;

public partial class FloatingWidget : Window
{
    public event Action<double, double>? Moved;

    public FloatingWidget()
    {
        InitializeComponent();
    }

    public void Bind(QuotaSnapshot snapshot)
    {
        Label.Text = $"Pro {DisplayFormatting.UsageLabel(snapshot)} | XH {snapshot.Reasoning.ExtraHigh}";
    }

    public void Apply(AppSettings settings)
    {
        Left = settings.WidgetLeft;
        Top = settings.WidgetTop;
        Opacity = settings.WidgetOpacity;
        Topmost = settings.WidgetAlwaysOnTop;
        SetClickThrough(settings.WidgetClickThrough);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
            Moved?.Invoke(Left, Top);
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
