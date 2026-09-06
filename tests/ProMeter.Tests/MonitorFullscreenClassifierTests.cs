using ProMeter.Codex;

namespace ProMeter.Tests;

/// <summary>
/// Regression coverage for the multi-monitor fullscreen suppression bug: ProMeter used to
/// trust GetForegroundWindow() alone, so switching focus to a normal window on another
/// monitor was misread as "fullscreen exited" on the monitor that actually still had a
/// fullscreen window. MonitorFullscreenClassifier instead scans every eligible top-level
/// window and asks only whether the TARGET monitor is still covered.
/// </summary>
public class MonitorFullscreenClassifierTests
{
    private static readonly ScreenRect TargetMonitor4K = new(0, 0, 3840, 2160);
    private static readonly ScreenRect TargetWorkArea4K = new(0, 0, 3840, 2112);

    // A: fullscreen window on the target monitor; foreground moves to a normal window on a
    // secondary monitor to the right. The strip must stay hidden, and must stay hidden
    // indefinitely (not just for a few polls) because the real fullscreen window never left.
    [Fact]
    public void A_ForegroundMovesToSecondaryMonitorWindow_StripStaysHiddenIndefinitely()
    {
        var candidates = new[]
        {
            WindowCandidateFacts.Application(TargetMonitor4K, maximized: false, framed: false, popup: true, isForeground: false, className: "GameWindowClass"),
            WindowCandidateFacts.Application(new ScreenRect(4099, 109, 1920, 1682), maximized: false, framed: true, popup: false, isForeground: true, className: "Chrome_WidgetWin_1")
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.Fullscreen, observation.Kind);
        Assert.Equal("covers-monitor-over-taskbar", observation.Reason);
        Assert.Equal("GameWindowClass", observation.Foreground.ClassName);
        Assert.False(observation.CandidateWasForeground);

        var gate = new TaskbarStripVisibilityGate();
        // Prime it into a visible state, then apply the fullscreen observation repeatedly —
        // far past the old exit-confirmation threshold — and confirm it never shows again.
        Assert.Equal(TaskbarStripVisibilityAction.Show, gate.Observe(Normal(), TaskbarStripMode.Full, FullscreenObservation.FromFlag(false)).Action);
        for (var i = 0; i < 50; i++)
        {
            var decision = gate.Observe(Hidden(), TaskbarStripMode.Hidden, observation);
            Assert.False(decision.OverlayVisible);
        }

        Assert.True(gate.FullscreenSuppressed);
    }

    // B: same fullscreen target-monitor window, but foreground moves to Chrome on a portrait
    // monitor to the left. Still must stay hidden.
    [Fact]
    public void B_ForegroundMovesToPortraitMonitorWindow_StripStaysHidden()
    {
        var candidates = new[]
        {
            WindowCandidateFacts.Application(TargetMonitor4K, maximized: false, framed: false, popup: true, isForeground: false, className: "GameWindowClass"),
            WindowCandidateFacts.Application(new ScreenRect(-2160, -698, 2160, 3792), maximized: true, framed: true, popup: false, isForeground: true, className: "Chrome_WidgetWin_1")
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.Fullscreen, observation.Kind);

        var gate = SuppressedGate();
        for (var i = 0; i < 10; i++)
        {
            Assert.False(gate.Observe(Hidden(), TaskbarStripMode.Hidden, observation).OverlayVisible);
        }
    }

    // C: fullscreen target-monitor window present; Shell_TrayWnd becomes foreground (e.g. the
    // user clicked the taskbar). Shell chrome must never be read as fullscreen-exit evidence.
    [Fact]
    public void C_ShellTrayBecomesForeground_StripStaysHidden()
    {
        var candidates = new WindowCandidateFacts[]
        {
            WindowCandidateFacts.Application(TargetMonitor4K, maximized: false, framed: false, popup: true, className: "GameWindowClass"),
            new WindowCandidateFacts(
                ClassName: "Shell_TrayWnd",
                Bounds: new ScreenRect(0, 2112, 3840, 48),
                BoundsValid: true,
                Visible: true,
                Minimized: false,
                Cloaked: false,
                Maximized: false,
                NormallyFramed: false,
                Popup: false,
                IsForeground: true,
                Role: ForegroundWindowRole.ShellTaskbar)
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.Fullscreen, observation.Kind);

        var gate = SuppressedGate();
        Assert.False(gate.Observe(Hidden(), TaskbarStripMode.Hidden, observation).OverlayVisible);
    }

    // D: fullscreen target-monitor window present; ProMeter's own flyout/overlay becomes
    // foreground. Our own windows must never count as fullscreen-exit evidence.
    [Fact]
    public void D_OwnOverlayBecomesForeground_StripStaysHidden()
    {
        var candidates = new WindowCandidateFacts[]
        {
            WindowCandidateFacts.Application(TargetMonitor4K, maximized: false, framed: false, popup: true, className: "GameWindowClass"),
            new WindowCandidateFacts(
                ClassName: "HwndWrapper[prometer.exe;;guid]",
                Bounds: new ScreenRect(3400, 1900, 360, 400),
                BoundsValid: true,
                Visible: true,
                Minimized: false,
                Cloaked: false,
                Maximized: false,
                NormallyFramed: false,
                Popup: false,
                IsForeground: true,
                Role: ForegroundWindowRole.OwnOverlay)
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.Fullscreen, observation.Kind);

        var gate = SuppressedGate();
        Assert.False(gate.Observe(Hidden(), TaskbarStripMode.Hidden, observation).OverlayVisible);
    }

    // E: the target-monitor fullscreen window actually disappears (closed / alt-F4'd) and only
    // a normal, non-covering window remains on that monitor. Only now, after the gate's stable
    // confirmation streak, may the strip show again.
    [Fact]
    public void E_FullscreenWindowActuallyGone_StripShowsOnlyAfterStableConfirmations()
    {
        var normalRemaining = new[]
        {
            WindowCandidateFacts.Application(new ScreenRect(200, 200, 1200, 800), maximized: false, framed: true, popup: false, className: "Notepad")
        };

        var observation = MonitorFullscreenClassifier.Observe(normalRemaining, TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);

        var gate = SuppressedGate();
        for (var i = 1; i <= TaskbarStripVisibilityGate.FullscreenExitConfirmationsRequired - 1; i++)
        {
            var decision = gate.Observe(Hidden(), TaskbarStripMode.Hidden, observation);
            Assert.False(decision.OverlayVisible);
            Assert.True(gate.FullscreenSuppressed);
        }

        var restored = gate.Observe(Normal(), TaskbarStripMode.Full, observation);
        Assert.Equal(TaskbarStripVisibilityAction.Show, restored.Action);
        Assert.True(restored.OverlayVisible);
        Assert.False(gate.FullscreenSuppressed);
    }

    // F: a normal window is maximized to the work area only (taskbar still reserves space).
    // That is ordinary maximized behavior, not fullscreen — the strip may show.
    [Fact]
    public void F_MaximizedNormalWindowFillsOnlyWorkArea_IsNotFullscreen()
    {
        var candidates = new[]
        {
            WindowCandidateFacts.Application(TargetWorkArea4K, maximized: true, framed: true, popup: false, className: "Chrome_WidgetWin_1")
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);

        var gate = new TaskbarStripVisibilityGate();
        var decision = gate.Observe(Normal(), TaskbarStripMode.Full, observation);
        Assert.Equal(TaskbarStripVisibilityAction.Show, decision.Action);
    }

    // F variant: taskbar auto-hidden so a maximized normal window's bounds equal the monitor.
    // Still just a maximized window (Maximized=true), not real fullscreen content.
    [Fact]
    public void F_MaximizedNormalWindowWithAutoHiddenTaskbar_IsNotFullscreen()
    {
        var candidates = new[]
        {
            WindowCandidateFacts.Application(TargetMonitor4K, maximized: true, framed: true, popup: false, className: "Chrome_WidgetWin_1")
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, TargetMonitor4K, TargetMonitor4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);
    }

    // G: a borderless (popup, unframed) window that exactly covers the target monitor is
    // fullscreen, regardless of whether the taskbar reserves space.
    [Fact]
    public void G_BorderlessWindowCoveringMonitor_IsFullscreen()
    {
        var candidates = new[]
        {
            WindowCandidateFacts.Application(TargetMonitor4K, maximized: false, framed: false, popup: true, className: "GameWindowClass")
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.Fullscreen, observation.Kind);
        Assert.Equal("covers-monitor-over-taskbar", observation.Reason);
    }

    // H: a fullscreen window exists only on a secondary monitor while the strip lives on the
    // primary monitor. The primary monitor's classification must be based only on primary
    // monitor windows — the secondary monitor's fullscreen state must not leak across.
    [Fact]
    public void H_FullscreenOnSecondaryMonitorOnly_DoesNotAffectPrimaryMonitorClassification()
    {
        var primaryMonitor = new ScreenRect(0, 0, 1920, 1080);
        var primaryWorkArea = new ScreenRect(0, 0, 1920, 1040);
        var secondaryFullscreen = new ScreenRect(1920, 0, 1920, 1080);

        var candidates = new[]
        {
            WindowCandidateFacts.Application(secondaryFullscreen, maximized: false, framed: false, popup: true, isForeground: true, className: "GameWindowClass"),
            WindowCandidateFacts.Application(new ScreenRect(100, 100, 800, 600), maximized: false, framed: true, popup: false, className: "Notepad")
        };

        var observation = MonitorFullscreenClassifier.Observe(candidates, primaryMonitor, primaryWorkArea, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);

        var gate = new TaskbarStripVisibilityGate();
        var decision = gate.Observe(Normal() with { Monitor = primaryMonitor, WorkArea = primaryWorkArea }, TaskbarStripMode.Full, observation);
        Assert.Equal(TaskbarStripVisibilityAction.Show, decision.Action);
    }

    [Fact]
    public void EnumerationFailure_IsUnknown_NotConfirmedExit()
    {
        var observation = MonitorFullscreenClassifier.Observe([], TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: false);
        Assert.Equal(FullscreenObservationKind.Unknown, observation.Kind);
        Assert.Equal("enumeration-failed", observation.Reason);

        var gate = SuppressedGate();
        Assert.False(gate.Observe(Hidden(), TaskbarStripMode.Hidden, observation).OverlayVisible);
        Assert.True(gate.FullscreenSuppressed);
        Assert.Equal(0, gate.FullscreenExitConfirmationStreak);
    }

    // D: fullscreen suppression is active and enumeration keeps failing (Unknown) on every
    // poll. A repeated Unknown must never accumulate toward the exit-confirmation streak, no
    // matter how many times it repeats — the strip must stay hidden indefinitely.
    [Fact]
    public void D_RepeatedEnumerationFailureWhileSuppressed_NeverAccumulatesExitStreak()
    {
        var observation = MonitorFullscreenClassifier.Observe([], TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: false);
        Assert.Equal(FullscreenObservationKind.Unknown, observation.Kind);

        var gate = SuppressedGate();
        for (var i = 0; i < 25; i++)
        {
            var decision = gate.Observe(Hidden(), TaskbarStripMode.Hidden, observation);
            Assert.False(decision.OverlayVisible);
            Assert.True(gate.FullscreenSuppressed);
            Assert.Equal(0, gate.FullscreenExitConfirmationStreak);
        }
    }

    [Fact]
    public void NoWindowsAtAllOnMonitor_IsConfirmedNotFullscreen()
    {
        var observation = MonitorFullscreenClassifier.Observe([], TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);
        Assert.Equal("no-application-window-on-monitor", observation.Reason);
    }

    [Fact]
    public void MinimizedOrCloakedOrInvisibleCoveringWindow_IsIgnored()
    {
        var minimized = WindowCandidateFacts.Application(TargetMonitor4K, framed: false, popup: true) with { Minimized = true };
        var cloaked = WindowCandidateFacts.Application(TargetMonitor4K, framed: false, popup: true) with { Cloaked = true };
        var invisible = WindowCandidateFacts.Application(TargetMonitor4K, framed: false, popup: true) with { Visible = false };
        var zeroSize = WindowCandidateFacts.Application(new ScreenRect(0, 0, 0, 0), framed: false, popup: true);

        foreach (var candidate in new[] { minimized, cloaked, invisible, zeroSize })
        {
            var observation = MonitorFullscreenClassifier.Observe([candidate], TargetMonitor4K, TargetWorkArea4K, enumerationSucceeded: true);
            Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);
        }
    }

    [Fact]
    public void NonApplicationShellClasses_AreExcludedFromEnumeration()
    {
        Assert.True(TaskbarStatusPositioner.IsNonApplicationShellClass("Windows.UI.Core.CoreWindow"));
        Assert.True(TaskbarStatusPositioner.IsNonApplicationShellClass("TaskSwitcherWnd"));
        Assert.True(TaskbarStatusPositioner.IsNonApplicationShellClass("MultitaskingViewFrame"));
        Assert.True(TaskbarStatusPositioner.IsNonApplicationShellClass("Shell_TrayWnd"));
        Assert.True(TaskbarStatusPositioner.IsNonApplicationShellClass("Progman"));
        Assert.False(TaskbarStatusPositioner.IsNonApplicationShellClass("Chrome_WidgetWin_1"));
        Assert.False(TaskbarStatusPositioner.IsNonApplicationShellClass(null));

        Assert.Equal(
            ForegroundWindowRole.Desktop,
            TaskbarStatusPositioner.ClassifyForegroundRole("Windows.UI.Core.CoreWindow", ownOverlay: false, taskbarWindow: false));
    }

    // A: EnumWindows itself reported failure and nothing was collected -> failed.
    [Fact]
    public void A_EnumWindowsFailed_NoCandidates_EnumerationFails()
    {
        Assert.False(WindowEnumerationOutcome.Succeeded(enumerationApiReturnedTrue: false, candidateCount: 0));
    }

    // B: this is the actual bug. EnumWindows reported failure, but some candidates were
    // collected before it failed (a partial scan). That must NOT be treated as a successful,
    // exhaustive enumeration — the real fullscreen window on the target monitor could be
    // exactly the one that was never reached.
    [Fact]
    public void B_EnumWindowsFailed_WithPartialCandidates_StillFails()
    {
        Assert.False(WindowEnumerationOutcome.Succeeded(enumerationApiReturnedTrue: false, candidateCount: 37));
    }

    // C: EnumWindows reported success with zero candidates (a legitimately empty desktop) ->
    // the classifier is free to conclude ConfirmedNotFullscreen from that.
    [Fact]
    public void C_EnumWindowsSucceeded_NoCandidates_EnumerationSucceeds()
    {
        Assert.True(WindowEnumerationOutcome.Succeeded(enumerationApiReturnedTrue: true, candidateCount: 0));
    }

    [Fact]
    public void EnumWindowsSucceeded_WithCandidates_EnumerationSucceeds()
    {
        Assert.True(WindowEnumerationOutcome.Succeeded(enumerationApiReturnedTrue: true, candidateCount: 12));
    }

    // Win32 adapter regression: TaskbarWin32.ObserveFullscreen must route EnumWindows' own
    // result through WindowEnumerationOutcome instead of re-deriving success from how many
    // candidates happened to be collected. This is a source check because TaskbarWin32 is a
    // Windows-only internal class in the WPF project that this Core test project cannot
    // reference or instantiate directly.
    [Fact]
    public void Win32Adapter_NeverPromotesFailedEnumerationUsingCandidateCount()
    {
        var source = File.ReadAllText(Find("src/ProMeter/UI/TaskbarWin32.cs"));
        var method = source[source.IndexOf("private static FullscreenObservation ObserveFullscreen", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("private static IntPtr ForegroundRoot", StringComparison.Ordinal)];

        Assert.Contains("WindowEnumerationOutcome.Succeeded(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("candidates.Count > 0", method, StringComparison.Ordinal);
    }

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }

    private static TaskbarStripVisibilityGate SuppressedGate()
    {
        var gate = new TaskbarStripVisibilityGate();
        Assert.Equal(TaskbarStripVisibilityAction.Show, gate.Observe(Normal(), TaskbarStripMode.Full, FullscreenObservation.FromFlag(false)).Action);
        var enter = gate.Observe(Hidden(), TaskbarStripMode.Hidden, FullscreenObservation.FromFlag(true));
        Assert.Equal(TaskbarStripVisibilityAction.Hide, enter.Action);
        Assert.True(gate.FullscreenSuppressed);
        return gate;
    }

    private static TaskbarLayoutInput Normal() => new(
        TargetMonitor4K,
        TargetWorkArea4K,
        new ScreenRect(0, 2112, 3840, 48),
        new ScreenRect(3600, 2112, 200, 48),
        new ScreenRect(3700, 2112, 80, 48),
        new ScreenRect(0, 2112, 3200, 48),
        TaskbarEdge.Bottom,
        1,
        false,
        true,
        false);

    private static TaskbarLayoutInput Hidden() => Normal() with { ExclusiveFullscreenOnMonitor = true };
}
