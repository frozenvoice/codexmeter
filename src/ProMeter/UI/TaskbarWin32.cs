using System.Runtime.InteropServices;
using ProMeter.Codex;

namespace ProMeter.UI;

internal readonly record struct TaskbarCapture(TaskbarLayoutInput Input, FullscreenObservation Fullscreen);

internal static class TaskbarWin32
{
    private const uint AbmGetTaskbarPos = 5;
    private const uint AbmGetState = 4;
    private const int AbsAutoHide = 1;
    private const int GwlStyle = -16;
    private const int WsMaximize = 0x01000000;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const uint GaRoot = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
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

    public static TaskbarCapture Capture(IntPtr stripHwnd)
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
        var fullscreen = ObserveFullscreen(monitor.Monitor, work, stripHwnd, taskbarHwnd);
        var input = new TaskbarLayoutInput(
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
            fullscreen.Kind == FullscreenObservationKind.Fullscreen);
        return new TaskbarCapture(input, fullscreen);
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

    /// <summary>
    /// Monitor-scoped fullscreen detection. Enumerates every eligible top-level window
    /// (via EnumWindows) instead of trusting GetForegroundWindow() alone, so a normal window
    /// becoming foreground on a DIFFERENT monitor can never be misread as fullscreen exiting
    /// on the monitor the taskbar strip actually lives on. GetForegroundWindow is consulted
    /// only to tag which enumerated candidate happens to be foreground, for diagnostics —
    /// it never gates the classification itself.
    /// </summary>
    private static FullscreenObservation ObserveFullscreen(
        ScreenRect monitor,
        ScreenRect work,
        IntPtr stripHwnd,
        IntPtr taskbarHwnd)
    {
        var foregroundRoot = ForegroundRoot();
        var currentProcessId = GetCurrentProcessId();
        var candidates = new List<WindowCandidateFacts>();
        var enumerationSucceeded = true;
        try
        {
            if (!EnumWindows(
                    (hwnd, _) =>
                    {
                        candidates.Add(BuildCandidate(hwnd, foregroundRoot, stripHwnd, taskbarHwnd, currentProcessId));
                        return true;
                    },
                    IntPtr.Zero))
            {
                enumerationSucceeded = candidates.Count > 0;
            }
        }
        catch
        {
            enumerationSucceeded = false;
        }

        return MonitorFullscreenClassifier.Observe(candidates, monitor, work, enumerationSucceeded);
    }

    private static IntPtr ForegroundRoot()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var root = GetAncestor(foreground, GaRoot);
        return root != IntPtr.Zero ? root : foreground;
    }

    private static WindowCandidateFacts BuildCandidate(
        IntPtr hwnd,
        IntPtr foregroundRoot,
        IntPtr stripHwnd,
        IntPtr taskbarHwnd,
        uint currentProcessId)
    {
        var className = WindowClassName(hwnd);
        GetWindowThreadProcessId(hwnd, out var ownerProcessId);
        var isOwnProcess = ownerProcessId != 0 && ownerProcessId == currentProcessId;
        var isOwnOverlay = isOwnProcess || (stripHwnd != IntPtr.Zero && hwnd == stripHwnd);
        var isTaskbarWindow = taskbarHwnd != IntPtr.Zero && hwnd == taskbarHwnd;
        var role = TaskbarStatusPositioner.ClassifyForegroundRole(className, isOwnOverlay, isTaskbarWindow);
        var style = GetWindowLong(hwnd, GwlStyle);
        var bounds = FrameBounds(hwnd, out var boundsValid);
        return new WindowCandidateFacts(
            className,
            bounds,
            boundsValid,
            IsWindowVisible(hwnd),
            IsIconic(hwnd),
            IsCloaked(hwnd),
            (style & WsMaximize) == WsMaximize,
            (style & (WsCaption | WsThickFrame)) != 0,
            (style & WsPopup) != 0,
            hwnd == foregroundRoot,
            role);
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return DwmGetWindowAttributeInt(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static ScreenRect FrameBounds(IntPtr hwnd, out bool valid)
    {
        try
        {
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<RECT>()) == 0
                && frame.Right > frame.Left
                && frame.Bottom > frame.Top)
            {
                valid = true;
                return ToRect(frame);
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        valid = GetWindowRect(hwnd, out var rect);
        return valid ? ToRect(rect) : default;
    }

    private static string WindowClassName(IntPtr hwnd)
    {
        var buffer = new System.Text.StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : "";
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
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

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
