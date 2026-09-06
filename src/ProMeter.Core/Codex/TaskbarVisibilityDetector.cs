namespace ProMeter.Codex;

public static class TaskbarVisibilityDetector
{
    public const int RevealedThicknessPx = 20;
    public const int HiddenPeekThicknessPx = 4;
    public const int FullscreenPollMilliseconds = 250;
    public const int FullscreenTolerancePx = 4;

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

    public static bool HasUsableTaskbarRect(ScreenRect taskbar) =>
        taskbar.Width > 0 && taskbar.Height > 0;

    public static bool IsMalformedCapture(TaskbarLayoutInput input) =>
        input.Monitor.Width <= 0
        || input.Monitor.Height <= 0
        || !HasUsableTaskbarRect(input.Taskbar);

    public static bool IsRevealed(
        bool autoHide,
        ScreenRect taskbar,
        ScreenRect monitor,
        TaskbarEdge edge)
    {
        if (!HasUsableTaskbarRect(taskbar))
        {
            return false;
        }

        if (!autoHide)
        {
            return true;
        }

        return ExposedThickness(taskbar, monitor, edge) >= RevealedThicknessPx;
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

public enum TaskbarStripVisibilityAction
{
    Show,
    Hide,
    Retain
}

public readonly record struct TaskbarStripVisibilityDecision(
    TaskbarStripVisibilityAction Action,
    bool OverlayVisible,
    string? LogLine);

public sealed class TaskbarStripVisibilityGate
{
    public const int ConsecutiveHiddenRequired = 3;

    private int _hiddenStreak;
    private bool _overlayVisible;
    private string? _lastSignature;

    public bool OverlayVisible => _overlayVisible;

    public TaskbarStripVisibilityDecision Observe(TaskbarLayoutInput input, TaskbarStripMode mode)
    {
        var classified = Classify(input);
        TaskbarStripVisibilityAction action;
        if (classified.ImmediateHide)
        {
            _hiddenStreak = 0;
            action = _overlayVisible ? TaskbarStripVisibilityAction.Hide : TaskbarStripVisibilityAction.Retain;
            _overlayVisible = false;
        }
        else if (classified.InvalidGeometry)
        {
            _hiddenStreak = 0;
            action = TaskbarStripVisibilityAction.Retain;
        }
        else if (classified.WantVisible)
        {
            _hiddenStreak = 0;
            action = _overlayVisible ? TaskbarStripVisibilityAction.Retain : TaskbarStripVisibilityAction.Show;
            _overlayVisible = true;
        }
        else
        {
            _hiddenStreak++;
            if (_overlayVisible && _hiddenStreak < ConsecutiveHiddenRequired)
            {
                action = TaskbarStripVisibilityAction.Retain;
            }
            else
            {
                action = _overlayVisible ? TaskbarStripVisibilityAction.Hide : TaskbarStripVisibilityAction.Retain;
                _overlayVisible = false;
                _hiddenStreak = 0;
            }
        }

        var log = BuildLog(action, classified, mode, input);
        return new TaskbarStripVisibilityDecision(action, _overlayVisible, log);
    }

    public static TaskbarStripSample Classify(TaskbarLayoutInput input)
    {
        if (input.ExclusiveFullscreenOnMonitor)
        {
            return new TaskbarStripSample(false, true, false, "exclusive-fullscreen");
        }

        if (TaskbarVisibilityDetector.IsMalformedCapture(input))
        {
            return new TaskbarStripSample(false, false, true, "transient-invalid-geometry");
        }

        if (!input.TaskbarVisible)
        {
            return new TaskbarStripSample(
                false,
                false,
                false,
                input.TaskbarAutoHide ? "auto-hide-collapsed" : "shell-invisible");
        }

        if (input.TaskbarAutoHide
            && !TaskbarVisibilityDetector.IsRevealed(true, input.Taskbar, input.Monitor, input.Edge))
        {
            return new TaskbarStripSample(false, false, false, "auto-hide-collapsed");
        }

        if (!TaskbarVisibilityDetector.ShouldShowOverlay(
                input.ExclusiveFullscreenOnMonitor,
                input.TaskbarAutoHide,
                input.TaskbarVisible,
                input.Taskbar,
                input.Monitor,
                input.Edge))
        {
            return new TaskbarStripSample(false, false, false, "not-revealed");
        }

        return new TaskbarStripSample(true, false, false, "revealed");
    }

    private string? BuildLog(
        TaskbarStripVisibilityAction action,
        TaskbarStripSample sample,
        TaskbarStripMode mode,
        TaskbarLayoutInput input)
    {
        string line;
        if (action == TaskbarStripVisibilityAction.Show)
        {
            line =
                $"taskbar strip shown mode={mode} autoHide={input.TaskbarAutoHide} shellVisible={input.TaskbarVisible} edge={input.Edge}";
        }
        else if (action == TaskbarStripVisibilityAction.Hide)
        {
            line = $"taskbar strip hidden reason={sample.Reason}";
            if (sample.ImmediateHide)
            {
                line += $" foregroundCoversMonitor=true taskbarVisible={(input.TaskbarVisible ? "true" : "false")}";
            }
        }
        else if (sample.InvalidGeometry && _overlayVisible)
        {
            line = $"taskbar strip retained reason={sample.Reason}";
        }
        else
        {
            return null;
        }

        if (string.Equals(line, _lastSignature, StringComparison.Ordinal))
        {
            return null;
        }

        _lastSignature = line;
        return line;
    }
}

public readonly record struct TaskbarStripSample(
    bool WantVisible,
    bool ImmediateHide,
    bool InvalidGeometry,
    string Reason);
