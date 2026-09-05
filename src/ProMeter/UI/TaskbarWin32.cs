using System.Runtime.InteropServices;
using ProMeter.Codex;

namespace ProMeter.UI;

internal static class TaskbarWin32
{
    private const uint AbmGetTaskbarPos = 5;
    private const uint AbmGetState = 4;
    private const int AbsAutoHide = 1;
    public const int WmDisplayChange = 0x007E;
    public const int WmDpiChanged = 0x02E0;
    public const int WmSettingChange = 0x001A;
    public const int WmThemeChanged = 0x031A;
    public const int WmWtsSessionChange = 0x02B1;
    public const int GwlExStyle = -20;
    public const int WsExToolwindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;
    public const int WsExTransparent = 0x00000020;

    public static int TaskbarCreatedMessage { get; } = RegisterWindowMessage("TaskbarCreated");

    public static TaskbarLayoutInput Capture(IntPtr stripHwnd)
    {
        var taskbarHwnd = FindWindow("Shell_TrayWnd", null);
        var pos = GetTaskbarPos(taskbarHwnd);
        var actual = taskbarHwnd != IntPtr.Zero && GetWindowRect(taskbarHwnd, out var trayRect)
            ? ToRect(trayRect)
            : pos.Bounds;
        var monitor = MonitorFromPoint(new POINT { X = actual.X + 1, Y = actual.Y + 1 });
        var work = WorkArea(monitor);
        var scale = DpiScale(stripHwnd != IntPtr.Zero ? stripHwnd : taskbarHwnd);
        var notify = ChildRect(taskbarHwnd, "TrayNotifyWnd");
        var clock = ChildRect(notify?.Hwnd ?? IntPtr.Zero, "TrayClockWClass")
                    ?? ChildRect(notify?.Hwnd ?? IntPtr.Zero, "ClockButton");
        var taskList = ChildRect(taskbarHwnd, "MSTaskListWClass")
                       ?? ChildRect(taskbarHwnd, "MSTaskSwWClass");
        var fullscreen = ExclusiveFullscreen(monitor.Monitor, work, pos.Bounds);
        return new TaskbarLayoutInput(
            monitor.Monitor,
            work,
            actual,
            notify?.Rect,
            clock?.Rect,
            taskList?.Rect,
            pos.Edge,
            scale,
            IsAutoHide(),
            IsWindowVisible(taskbarHwnd),
            fullscreen);
    }

    public static void ApplyToolWindowStyles(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GwlExStyle);
        style |= WsExToolwindow | WsExNoActivate;
        style &= ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    public static bool ReassertTopmostNoActivate(IntPtr hwnd, out int error)
    {
        error = 0;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var ok = SetWindowPos(
            hwnd,
            TaskbarTopmostPlacement.HwndTopmost,
            0,
            0,
            0,
            0,
            TaskbarTopmostPlacement.Flags);
        if (!ok)
        {
            error = Marshal.GetLastWin32Error();
        }

        return ok;
    }

    public static bool IsAutoHide()
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        return (SHAppBarMessage(AbmGetState, ref data).ToInt32() & AbsAutoHide) != 0;
    }

    private static (ScreenRect Bounds, TaskbarEdge Edge) GetTaskbarPos(IntPtr taskbarHwnd)
    {
        var data = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        if (SHAppBarMessage(AbmGetTaskbarPos, ref data) != IntPtr.Zero)
        {
            return (ToRect(data.rc), EdgeFromAbe(data.uEdge));
        }

        if (taskbarHwnd != IntPtr.Zero && GetWindowRect(taskbarHwnd, out var rect))
        {
            return (ToRect(rect), InferEdge(ToRect(rect)));
        }

        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                     ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return (new ScreenRect(0, screen.Bottom - 48, screen.Width, 48), TaskbarEdge.Bottom);
    }

    private static (ScreenRect Monitor, IntPtr Handle) MonitorFromPoint(POINT point)
    {
        var handle = MonitorFromPoint(point, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (handle != IntPtr.Zero && GetMonitorInfo(handle, ref info))
        {
            return (ToRect(info.rcMonitor), handle);
        }

        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                     ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        return (new ScreenRect(screen.X, screen.Y, screen.Width, screen.Height), IntPtr.Zero);
    }

    private static ScreenRect WorkArea((ScreenRect Monitor, IntPtr Handle) monitor)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor.Handle != IntPtr.Zero && GetMonitorInfo(monitor.Handle, ref info))
        {
            return ToRect(info.rcWork);
        }

        var area = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                   ?? new System.Drawing.Rectangle(0, 0, 1920, 1040);
        return new ScreenRect(area.X, area.Y, area.Width, area.Height);
    }

    private static (IntPtr Hwnd, ScreenRect Rect)? ChildRect(IntPtr parent, string className)
    {
        if (parent == IntPtr.Zero)
        {
            return null;
        }

        var child = FindWindowEx(parent, IntPtr.Zero, className, null);
        if (child == IntPtr.Zero || !GetWindowRect(child, out var rect))
        {
            return null;
        }

        return (child, ToRect(rect));
    }

    private static bool ExclusiveFullscreen(ScreenRect monitor, ScreenRect work, ScreenRect taskbar)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !GetWindowRect(foreground, out var rect))
        {
            return false;
        }

        var taskbarVisible = IsWindowVisible(FindWindow("Shell_TrayWnd", null));
        return TaskbarStatusPositioner.IsExclusiveFullscreen(ToRect(rect), monitor, work, taskbarVisible);
    }

    private static double DpiScale(IntPtr hwnd)
    {
        try
        {
            if (hwnd != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0)
                {
                    return dpi / 96d;
                }
            }
        }
        catch
        {
        }

        return 1;
    }

    private static ScreenRect ToRect(RECT rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private static TaskbarEdge EdgeFromAbe(int edge) => edge switch
    {
        0 => TaskbarEdge.Left,
        1 => TaskbarEdge.Top,
        2 => TaskbarEdge.Right,
        _ => TaskbarEdge.Bottom
    };

    private static TaskbarEdge InferEdge(ScreenRect bar)
    {
        if (bar.Width >= bar.Height)
        {
            return bar.Y <= 10 ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }

        return bar.X <= 10 ? TaskbarEdge.Left : TaskbarEdge.Right;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
