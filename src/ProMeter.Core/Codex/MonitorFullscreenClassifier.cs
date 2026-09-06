namespace ProMeter.Codex;

/// <summary>
/// One enumerated top-level window, as seen independently of whatever window currently
/// owns keyboard/mouse focus. <see cref="IsForeground"/> is carried only for diagnostics —
/// it must never gate the classification itself.
/// </summary>
public readonly record struct WindowCandidateFacts(
    string ClassName,
    ScreenRect Bounds,
    bool BoundsValid,
    bool Visible,
    bool Minimized,
    bool Cloaked,
    bool Maximized,
    bool NormallyFramed,
    bool Popup,
    bool IsForeground,
    ForegroundWindowRole Role)
{
    public static WindowCandidateFacts Application(
        ScreenRect bounds,
        bool maximized = false,
        bool framed = true,
        bool popup = false,
        bool isForeground = false,
        string className = "Application") =>
        new(className, bounds, true, true, false, false, maximized, framed, popup, isForeground, ForegroundWindowRole.Application);
}

/// <summary>
/// Whether a Win32 top-level window enumeration (EnumWindows) can be trusted as a complete,
/// exhaustive scan. A failed enumeration must never be promoted back to "succeeded" just
/// because some candidates were collected before the failure — a partial candidate set could
/// be missing exactly the window that matters (the real fullscreen window on the target
/// monitor), which would let a stale/partial scan masquerade as proof that fullscreen exited.
/// </summary>
public static class WindowEnumerationOutcome
{
    public static bool Succeeded(bool enumerationApiReturnedTrue, int candidateCount) =>
        enumerationApiReturnedTrue;
}

/// <summary>
/// Determines whether a single monitor is covered by a real fullscreen application window,
/// independent of the OS-global foreground window. The previous implementation trusted
/// GetForegroundWindow() alone, which is wrong on multi-monitor systems: a normal window on
/// another monitor becoming foreground must never be read as evidence that THIS monitor's
/// fullscreen content went away. Instead this scans every eligible top-level window and asks
/// only "is any of them still covering the target monitor?".
/// </summary>
public static class MonitorFullscreenClassifier
{
    public static FullscreenObservation Observe(
        IReadOnlyList<WindowCandidateFacts> candidates,
        ScreenRect monitor,
        ScreenRect workArea,
        bool enumerationSucceeded,
        int tolerancePx = TaskbarVisibilityDetector.FullscreenTolerancePx)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0)
        {
            return Unknown("invalid-monitor", monitor, workArea);
        }

        if (!enumerationSucceeded)
        {
            return Unknown("enumeration-failed", monitor, workArea);
        }

        var sawEligibleOnMonitor = false;

        foreach (var candidate in candidates)
        {
            if (candidate.Role != ForegroundWindowRole.Application)
            {
                // Shell chrome, our own windows, and the desktop carry no evidence either way.
                continue;
            }

            if (!candidate.Visible || candidate.Minimized || candidate.Cloaked)
            {
                continue;
            }

            if (!candidate.BoundsValid || candidate.Bounds.Width <= 0 || candidate.Bounds.Height <= 0)
            {
                continue;
            }

            if (!candidate.Bounds.Intersects(monitor))
            {
                // Lives entirely on a different monitor: irrelevant to this monitor's state.
                continue;
            }

            sawEligibleOnMonitor = true;

            if (!TaskbarStatusPositioner.CoversMonitor(candidate.Bounds, monitor, tolerancePx))
            {
                // On this monitor but not covering it — not a fullscreen candidate itself,
                // but it does not disprove another window's fullscreen coverage either.
                continue;
            }

            if (!TaskbarStatusPositioner.CoversMonitor(workArea, monitor, tolerancePx))
            {
                return Fullscreen(candidate, "covers-monitor-over-taskbar", monitor, workArea);
            }

            if (!candidate.NormallyFramed || candidate.Popup)
            {
                return Fullscreen(candidate, "borderless-covers-monitor", monitor, workArea);
            }

            if (candidate.Maximized)
            {
                // An ordinary maximized window whose bounds happen to equal the monitor
                // (e.g. taskbar auto-hidden) is still just a maximized window, not fullscreen.
                continue;
            }

            return Fullscreen(candidate, "framed-covers-monitor", monitor, workArea);
        }

        return new FullscreenObservation(
            FullscreenObservationKind.ConfirmedNotFullscreen,
            sawEligibleOnMonitor ? "no-covering-window-on-monitor" : "no-application-window-on-monitor",
            ForegroundWindowFacts.Missing(ForegroundWindowRole.Application),
            monitor,
            workArea);
    }

    private static FullscreenObservation Fullscreen(
        WindowCandidateFacts candidate,
        string reason,
        ScreenRect monitor,
        ScreenRect workArea)
    {
        var facts = new ForegroundWindowFacts(
            candidate.ClassName,
            candidate.Bounds,
            candidate.BoundsValid,
            candidate.Maximized,
            candidate.NormallyFramed,
            candidate.Popup,
            candidate.Role);
        return new FullscreenObservation(
            FullscreenObservationKind.Fullscreen,
            reason,
            facts,
            monitor,
            workArea,
            candidate.IsForeground);
    }

    private static FullscreenObservation Unknown(string reason, ScreenRect monitor, ScreenRect workArea) =>
        new(
            FullscreenObservationKind.Unknown,
            reason,
            ForegroundWindowFacts.Missing(ForegroundWindowRole.None),
            monitor,
            workArea);
}
