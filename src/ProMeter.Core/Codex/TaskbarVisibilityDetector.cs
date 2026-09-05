namespace ProMeter.Codex;

public static class TaskbarVisibilityDetector
{
    public const int RevealedThicknessPx = 20;
    public const int HiddenPeekThicknessPx = 4;

    public static int ExposedThickness(ScreenRect taskbar, ScreenRect monitor, TaskbarEdge edge)
    {
        var left = Math.Max(taskbar.X, monitor.X);
        var top = Math.Max(taskbar.Y, monitor.Y);
        var right = Math.Min(taskbar.Right, monitor.Right);
        var bottom = Math.Min(taskbar.Bottom, monitor.Bottom);
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        return edge is TaskbarEdge.Left or TaskbarEdge.Right
            ? right - left
            : bottom - top;
    }

    public static bool IsRevealed(
        bool autoHide,
        ScreenRect taskbar,
        ScreenRect monitor,
        TaskbarEdge edge)
    {
        var thickness = ExposedThickness(taskbar, monitor, edge);
        return thickness >= RevealedThicknessPx;
    }

    public static bool ShouldShowOverlay(
        bool exclusiveFullscreen,
        bool autoHide,
        bool shellWindowVisible,
        ScreenRect taskbar,
        ScreenRect monitor,
        TaskbarEdge edge)
    {
        if (exclusiveFullscreen || !shellWindowVisible)
        {
            return false;
        }

        return IsRevealed(autoHide, taskbar, monitor, edge);
    }
}
