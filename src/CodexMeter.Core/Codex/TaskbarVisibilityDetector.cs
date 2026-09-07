namespace CodexMeter.Codex;

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

public enum FullscreenObservationKind
{
    Fullscreen,
    ConfirmedNotFullscreen,
    Unknown
}

public enum ForegroundWindowRole
{
    Application,
    Desktop,
    ShellTaskbar,
    OwnOverlay,
    None
}

public readonly record struct ForegroundWindowFacts(
    string ClassName,
    ScreenRect Bounds,
    bool BoundsValid,
    bool Maximized,
    bool NormallyFramed,
    bool Popup,
    ForegroundWindowRole Role)
{
    public static ForegroundWindowFacts Missing(ForegroundWindowRole role) =>
        new("", default, false, false, false, false, role);

    public static ForegroundWindowFacts FromMaximizedFlag(ScreenRect bounds, bool maximized) =>
        new("", bounds, true, maximized, maximized, false, ForegroundWindowRole.Application);
}

public readonly record struct FullscreenObservation(
    FullscreenObservationKind Kind,
    string Reason,
    ForegroundWindowFacts Foreground,
    ScreenRect Monitor,
    ScreenRect WorkArea,
    bool CandidateWasForeground = false)
{
    public static FullscreenObservation FromFlag(bool exclusiveFullscreen) =>
        new(
            exclusiveFullscreen
                ? FullscreenObservationKind.Fullscreen
                : FullscreenObservationKind.ConfirmedNotFullscreen,
            exclusiveFullscreen ? "exclusive-fullscreen" : "flag-not-fullscreen",
            ForegroundWindowFacts.Missing(ForegroundWindowRole.Application),
            default,
            default);
}

public static class FullscreenClassifier
{
    public static FullscreenObservation Observe(
        ForegroundWindowFacts foreground,
        ScreenRect monitor,
        ScreenRect workArea,
        int tolerancePx = TaskbarVisibilityDetector.FullscreenTolerancePx)
    {
        FullscreenObservation Result(FullscreenObservationKind kind, string reason) =>
            new(kind, reason, foreground, monitor, workArea);

        if (monitor.Width <= 0 || monitor.Height <= 0)
        {
            return Result(FullscreenObservationKind.Unknown, "invalid-monitor");
        }

        switch (foreground.Role)
        {
            case ForegroundWindowRole.None:
                return Result(FullscreenObservationKind.Unknown, "no-foreground");
            case ForegroundWindowRole.OwnOverlay:
                return Result(FullscreenObservationKind.Unknown, "own-overlay-foreground");
            case ForegroundWindowRole.ShellTaskbar:
                return Result(FullscreenObservationKind.Unknown, "shell-taskbar-foreground");
            case ForegroundWindowRole.Desktop:
                return Result(FullscreenObservationKind.ConfirmedNotFullscreen, "desktop-foreground");
        }

        if (!foreground.BoundsValid || foreground.Bounds.Width <= 0 || foreground.Bounds.Height <= 0)
        {
            return Result(FullscreenObservationKind.Unknown, "invalid-foreground-bounds");
        }

        if (!TaskbarStatusPositioner.CoversMonitor(foreground.Bounds, monitor, tolerancePx))
        {
            return foreground.NormallyFramed
                ? Result(FullscreenObservationKind.ConfirmedNotFullscreen, "framed-window-foreground")
                : Result(FullscreenObservationKind.Unknown, "borderless-foreground-not-covering-monitor");
        }

        if (!TaskbarStatusPositioner.CoversMonitor(workArea, monitor, tolerancePx))
        {
            return Result(FullscreenObservationKind.Fullscreen, "covers-monitor-over-taskbar");
        }

        if (!foreground.NormallyFramed || foreground.Popup)
        {
            return Result(FullscreenObservationKind.Fullscreen, "borderless-covers-monitor");
        }

        return foreground.Maximized
            ? Result(FullscreenObservationKind.ConfirmedNotFullscreen, "framed-maximized-full-work-area")
            : Result(FullscreenObservationKind.Fullscreen, "framed-covers-monitor");
    }

    public static string KindLabel(FullscreenObservationKind kind) => kind switch
    {
        FullscreenObservationKind.Fullscreen => "fullscreen",
        FullscreenObservationKind.ConfirmedNotFullscreen => "not-fullscreen",
        _ => "unknown"
    };
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
    public const int FullscreenExitConfirmationsRequired = 4;

    private int _hiddenStreak;
    private bool _overlayVisible;
    private string? _lastSignature;
    private string? _lastSuppressedFields;
    private bool _fullscreenSuppressed;
    private int _fullscreenExitConfirmationStreak;

    public bool OverlayVisible => _overlayVisible;

    public bool FullscreenSuppressed => _fullscreenSuppressed;

    public int FullscreenExitConfirmationStreak => _fullscreenExitConfirmationStreak;

    public TaskbarStripVisibilityDecision Observe(
        TaskbarLayoutInput input,
        TaskbarStripMode mode,
        FullscreenObservation? fullscreen = null)
    {
        var observation = fullscreen ?? FullscreenObservation.FromFlag(input.ExclusiveFullscreenOnMonitor);
        var exitConfirmations = TrackFullscreenSuppression(observation);
        var classified = Classify(input, _fullscreenSuppressed, observation);
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

        var log = BuildLog(action, classified, mode, input, observation, exitConfirmations);
        return new TaskbarStripVisibilityDecision(action, _overlayVisible, log);
    }

    public static TaskbarStripSample Classify(TaskbarLayoutInput input) =>
        Classify(
            input,
            input.ExclusiveFullscreenOnMonitor,
            FullscreenObservation.FromFlag(input.ExclusiveFullscreenOnMonitor));

    private int TrackFullscreenSuppression(FullscreenObservation observation)
    {
        if (observation.Kind == FullscreenObservationKind.Fullscreen)
        {
            _fullscreenSuppressed = true;
            _fullscreenExitConfirmationStreak = 0;
            return 0;
        }

        if (!_fullscreenSuppressed || observation.Kind == FullscreenObservationKind.Unknown)
        {
            _fullscreenExitConfirmationStreak = 0;
            return 0;
        }

        _fullscreenExitConfirmationStreak++;
        if (_fullscreenExitConfirmationStreak < FullscreenExitConfirmationsRequired)
        {
            return 0;
        }

        var confirmations = _fullscreenExitConfirmationStreak;
        _fullscreenSuppressed = false;
        _fullscreenExitConfirmationStreak = 0;
        _lastSuppressedFields = null;
        return confirmations;
    }

    private static TaskbarStripSample Classify(
        TaskbarLayoutInput input,
        bool fullscreenSuppressed,
        FullscreenObservation observation)
    {
        if (fullscreenSuppressed)
        {
            return new TaskbarStripSample(false, true, false, SuppressionReason(observation.Kind));
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

    private static string SuppressionReason(FullscreenObservationKind kind) => kind switch
    {
        FullscreenObservationKind.Fullscreen => "exclusive-fullscreen",
        FullscreenObservationKind.ConfirmedNotFullscreen => "fullscreen-exit-pending",
        _ => "fullscreen-exit-unconfirmed"
    };

    private string? BuildLog(
        TaskbarStripVisibilityAction action,
        TaskbarStripSample sample,
        TaskbarStripMode mode,
        TaskbarLayoutInput input,
        FullscreenObservation observation,
        int exitConfirmations)
    {
        string line;
        if (action == TaskbarStripVisibilityAction.Show)
        {
            line =
                $"taskbar strip shown mode={mode} autoHide={input.TaskbarAutoHide} shellVisible={input.TaskbarVisible} edge={input.Edge}";
            if (exitConfirmations > 0)
            {
                line += $" fullscreenExitConfirmations={exitConfirmations}";
            }
        }
        else if (exitConfirmations > 0)
        {
            line = $"taskbar strip fullscreen exit confirmed confirmations={exitConfirmations} reason={sample.Reason}";
        }
        else if (action == TaskbarStripVisibilityAction.Hide)
        {
            line = $"taskbar strip hidden reason={sample.Reason}";
            if (sample.ImmediateHide)
            {
                line += $" taskbarVisible={Flag(input.TaskbarVisible)}{TrackSuppressedFields(observation)}";
            }
        }
        else if (sample.ImmediateHide)
        {
            var fields = ObservationFields(observation);
            if (string.Equals(fields, _lastSuppressedFields, StringComparison.Ordinal))
            {
                return null;
            }

            _lastSuppressedFields = fields;
            line = $"taskbar strip fullscreen retained reason={sample.Reason}{fields}";
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

    private string TrackSuppressedFields(FullscreenObservation observation)
    {
        var fields = ObservationFields(observation);
        _lastSuppressedFields = fields;
        return fields;
    }

    private static string ObservationFields(FullscreenObservation observation)
    {
        var foreground = observation.Foreground;
        var className = string.IsNullOrWhiteSpace(foreground.ClassName) ? "unknown" : foreground.ClassName;
        return $" observation={FullscreenClassifier.KindLabel(observation.Kind)}"
               + $" observationReason={observation.Reason}"
               + $" foregroundClass={className}"
               + $" foregroundRect={Rect(foreground.Bounds)}"
               + $" monitorRect={Rect(observation.Monitor)}"
               + $" workAreaRect={Rect(observation.WorkArea)}"
               + $" maximized={Flag(foreground.Maximized)}"
               + $" framed={Flag(foreground.NormallyFramed)}"
               + $" popup={Flag(foreground.Popup)}"
               + $" candidateWasForeground={Flag(observation.CandidateWasForeground)}"
               + " suppression=true";
    }

    private static string Rect(ScreenRect rect) => $"{rect.X},{rect.Y},{rect.Width}x{rect.Height}";

    private static string Flag(bool value) => value ? "true" : "false";
}

public readonly record struct TaskbarStripSample(
    bool WantVisible,
    bool ImmediateHide,
    bool InvalidGeometry,
    string Reason);
