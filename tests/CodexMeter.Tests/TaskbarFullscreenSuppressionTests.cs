using CodexMeter.Codex;

namespace CodexMeter.Tests;

public class TaskbarFullscreenSuppressionTests
{
    private static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);
    private static readonly ScreenRect TaskbarWorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void StartingDuringFullscreen_RecoversOnlyAfterConfirmedExit()
    {
        var gate = new TaskbarStripVisibilityGate();
        Assert.False(Observe(gate, FullscreenObservationKind.Fullscreen).OverlayVisible);
        for (var i = 1; i < TaskbarStripVisibilityGate.FullscreenExitConfirmationsRequired; i++)
            Assert.False(Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen).OverlayVisible);
        Assert.True(Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen).OverlayVisible);
    }

    [Fact]
    public void ExitConfirmationConstant_IsSeparateFromHiddenNoiseConstant()
    {
        Assert.Equal(4, TaskbarStripVisibilityGate.FullscreenExitConfirmationsRequired);
        Assert.Equal(3, TaskbarStripVisibilityGate.ConsecutiveHiddenRequired);
        Assert.NotEqual(
            TaskbarStripVisibilityGate.ConsecutiveHiddenRequired,
            TaskbarStripVisibilityGate.FullscreenExitConfirmationsRequired);
    }

    [Fact]
    public void A_FullscreenThenSingleFalseNegative_NeverShows()
    {
        var gate = Shown();
        var hide = Observe(gate, FullscreenObservationKind.Fullscreen);
        Assert.Equal(TaskbarStripVisibilityAction.Hide, hide.Action);
        Assert.True(gate.FullscreenSuppressed);

        var falseNegative = Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
        Assert.Equal(TaskbarStripVisibilityAction.Retain, falseNegative.Action);
        Assert.False(falseNegative.OverlayVisible);
        Assert.Equal(1, gate.FullscreenExitConfirmationStreak);

        var again = Observe(gate, FullscreenObservationKind.Fullscreen);
        Assert.Equal(TaskbarStripVisibilityAction.Retain, again.Action);
        Assert.False(again.OverlayVisible);
        Assert.True(gate.FullscreenSuppressed);
        Assert.Equal(0, gate.FullscreenExitConfirmationStreak);
    }

    [Fact]
    public void B_FullscreenThenUnknownThenFullscreen_NeverShows()
    {
        var gate = Shown();
        Assert.Equal(TaskbarStripVisibilityAction.Hide, Observe(gate, FullscreenObservationKind.Fullscreen).Action);

        foreach (var kind in new[]
                 {
                     FullscreenObservationKind.Unknown,
                     FullscreenObservationKind.Unknown,
                     FullscreenObservationKind.Fullscreen,
                     FullscreenObservationKind.Unknown
                 })
        {
            var decision = Observe(gate, kind);
            Assert.Equal(TaskbarStripVisibilityAction.Retain, decision.Action);
            Assert.False(decision.OverlayVisible);
            Assert.True(gate.FullscreenSuppressed);
            Assert.Equal(0, gate.FullscreenExitConfirmationStreak);
        }
    }

    [Fact]
    public void C_ThreeConfirmedExitSamples_StayHidden()
    {
        var gate = Shown();
        Observe(gate, FullscreenObservationKind.Fullscreen);
        for (var i = 1; i <= 3; i++)
        {
            var decision = Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
            Assert.Equal(TaskbarStripVisibilityAction.Retain, decision.Action);
            Assert.False(decision.OverlayVisible);
            Assert.True(gate.FullscreenSuppressed);
            Assert.Equal(i, gate.FullscreenExitConfirmationStreak);
        }
    }

    [Fact]
    public void D_FourthConfirmedExitSample_PermitsShow()
    {
        var gate = Shown();
        Observe(gate, FullscreenObservationKind.Fullscreen);
        for (var i = 1; i <= 3; i++)
        {
            Assert.False(Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen).OverlayVisible);
        }

        var restored = Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
        Assert.Equal(TaskbarStripVisibilityAction.Show, restored.Action);
        Assert.True(restored.OverlayVisible);
        Assert.False(gate.FullscreenSuppressed);
        Assert.Equal(0, gate.FullscreenExitConfirmationStreak);
        Assert.Contains("fullscreenExitConfirmations=4", restored.LogLine, StringComparison.Ordinal);
    }

    [Fact]
    public void E_UnknownSampleResetsExitStreak()
    {
        var gate = Shown();
        Observe(gate, FullscreenObservationKind.Fullscreen);
        Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
        Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
        Assert.Equal(2, gate.FullscreenExitConfirmationStreak);

        Observe(gate, FullscreenObservationKind.Unknown);
        Assert.Equal(0, gate.FullscreenExitConfirmationStreak);
        Assert.True(gate.FullscreenSuppressed);

        for (var i = 1; i <= 3; i++)
        {
            Assert.False(Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen).OverlayVisible);
        }

        var restored = Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
        Assert.Equal(TaskbarStripVisibilityAction.Show, restored.Action);
        Assert.True(restored.OverlayVisible);
    }

    [Fact]
    public void F_NormalStartup_ShowsImmediately()
    {
        var gate = new TaskbarStripVisibilityGate();
        var decision = Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
        Assert.Equal(TaskbarStripVisibilityAction.Show, decision.Action);
        Assert.True(decision.OverlayVisible);
        Assert.False(gate.FullscreenSuppressed);
        Assert.Equal(0, gate.FullscreenExitConfirmationStreak);
        Assert.DoesNotContain("fullscreenExitConfirmations", decision.LogLine ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void F_NormalStartupWithUnknownForeground_ShowsImmediately()
    {
        var gate = new TaskbarStripVisibilityGate();
        var decision = Observe(gate, FullscreenObservationKind.Unknown);
        Assert.Equal(TaskbarStripVisibilityAction.Show, decision.Action);
        Assert.True(decision.OverlayVisible);
        Assert.False(gate.FullscreenSuppressed);
    }

    [Fact]
    public void G_F11FromMaximizedStyle_IsFullscreen()
    {
        var observation = FullscreenClassifier.Observe(
            Application(Monitor, maximized: true, framed: false, popup: true),
            Monitor,
            Monitor);
        Assert.Equal(FullscreenObservationKind.Fullscreen, observation.Kind);
        Assert.Equal("borderless-covers-monitor", observation.Reason);

        var withoutPopupStyle = FullscreenClassifier.Observe(
            Application(Monitor, maximized: true, framed: false, popup: false),
            Monitor,
            Monitor);
        Assert.Equal(FullscreenObservationKind.Fullscreen, withoutPopupStyle.Kind);
    }

    [Fact]
    public void G_BorderlessFullscreenOverNormalTaskbar_IsFullscreen()
    {
        var observation = FullscreenClassifier.Observe(
            Application(Monitor, maximized: false, framed: false, popup: true),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.Fullscreen, observation.Kind);
        Assert.Equal("covers-monitor-over-taskbar", observation.Reason);
    }

    [Fact]
    public void H_NormalMaximizedWindow_IsConfirmedNotFullscreen()
    {
        var framedMaximized = FullscreenClassifier.Observe(
            Application(TaskbarWorkArea, maximized: true, framed: true, popup: false),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, framedMaximized.Kind);
        Assert.Equal("framed-window-foreground", framedMaximized.Reason);

        var autoHideMaximized = FullscreenClassifier.Observe(
            Application(Monitor, maximized: true, framed: true, popup: false),
            Monitor,
            Monitor);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, autoHideMaximized.Kind);
        Assert.Equal("framed-maximized-full-work-area", autoHideMaximized.Reason);
    }

    [Fact]
    public void H_FramedWindowedApp_IsConfirmedNotFullscreen()
    {
        var observation = FullscreenClassifier.Observe(
            Application(new ScreenRect(120, 80, 1200, 800), maximized: false, framed: true, popup: false),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);
    }

    [Fact]
    public void I_MalformedGeometryDuringFullscreen_StaysHidden()
    {
        var invalidBounds = FullscreenClassifier.Observe(
            new ForegroundWindowFacts("Chrome_WidgetWin_1", default, false, true, false, true, ForegroundWindowRole.Application),
            Monitor,
            Monitor);
        Assert.Equal(FullscreenObservationKind.Unknown, invalidBounds.Kind);
        Assert.Equal("invalid-foreground-bounds", invalidBounds.Reason);

        var invalidMonitor = FullscreenClassifier.Observe(
            Application(Monitor, maximized: false, framed: false, popup: true),
            default,
            default);
        Assert.Equal(FullscreenObservationKind.Unknown, invalidMonitor.Kind);
        Assert.Equal("invalid-monitor", invalidMonitor.Reason);

        var gate = Shown();
        Observe(gate, FullscreenObservationKind.Fullscreen);
        var retained = gate.Observe(Fullscreen(), TaskbarStripMode.Hidden, invalidBounds);
        Assert.Equal(TaskbarStripVisibilityAction.Retain, retained.Action);
        Assert.False(retained.OverlayVisible);
        Assert.True(gate.FullscreenSuppressed);
    }

    [Fact]
    public void J_TransientShellOrOverlayForeground_CannotProveExit()
    {
        var tray = FullscreenClassifier.Observe(
            ForegroundWindowFacts.Missing(ForegroundWindowRole.ShellTaskbar),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.Unknown, tray.Kind);

        var ownOverlay = FullscreenClassifier.Observe(
            ForegroundWindowFacts.Missing(ForegroundWindowRole.OwnOverlay),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.Unknown, ownOverlay.Kind);

        var missingForeground = FullscreenClassifier.Observe(
            ForegroundWindowFacts.Missing(ForegroundWindowRole.None),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.Unknown, missingForeground.Kind);

        var osd = FullscreenClassifier.Observe(
            Application(new ScreenRect(1700, 900, 180, 80), maximized: false, framed: false, popup: true),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.Unknown, osd.Kind);
        Assert.Equal("borderless-foreground-not-covering-monitor", osd.Reason);

        var gate = Shown();
        Observe(gate, FullscreenObservationKind.Fullscreen);
        foreach (var observation in new[] { tray, ownOverlay, missingForeground, osd })
        {
            var decision = gate.Observe(Fullscreen(), TaskbarStripMode.Hidden, observation);
            Assert.Equal(TaskbarStripVisibilityAction.Retain, decision.Action);
            Assert.False(decision.OverlayVisible);
            Assert.True(gate.FullscreenSuppressed);
        }
    }

    [Fact]
    public void DesktopForeground_ConfirmsNotFullscreen()
    {
        var desktop = FullscreenClassifier.Observe(
            ForegroundWindowFacts.Missing(ForegroundWindowRole.Desktop),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, desktop.Kind);
        Assert.Equal("desktop-foreground", desktop.Reason);
    }

    [Fact]
    public void ForegroundRoles_MapShellDesktopAndOwnOverlay()
    {
        Assert.Equal(
            ForegroundWindowRole.OwnOverlay,
            TaskbarStatusPositioner.ClassifyForegroundRole("Chrome_WidgetWin_1", true, false));
        Assert.Equal(
            ForegroundWindowRole.ShellTaskbar,
            TaskbarStatusPositioner.ClassifyForegroundRole("Shell_TrayWnd", false, false));
        Assert.Equal(
            ForegroundWindowRole.ShellTaskbar,
            TaskbarStatusPositioner.ClassifyForegroundRole("Shell_SecondaryTrayWnd", false, false));
        Assert.Equal(
            ForegroundWindowRole.ShellTaskbar,
            TaskbarStatusPositioner.ClassifyForegroundRole("Chrome_WidgetWin_1", false, true));
        Assert.Equal(
            ForegroundWindowRole.Desktop,
            TaskbarStatusPositioner.ClassifyForegroundRole("Progman", false, false));
        Assert.Equal(
            ForegroundWindowRole.Desktop,
            TaskbarStatusPositioner.ClassifyForegroundRole("WorkerW", false, false));
        Assert.Equal(
            ForegroundWindowRole.Application,
            TaskbarStatusPositioner.ClassifyForegroundRole("Chrome_WidgetWin_1", false, false));
        Assert.True(TaskbarStatusPositioner.IsIgnoredFullscreenForeground("Shell_TrayWnd"));
        Assert.True(TaskbarStatusPositioner.IsIgnoredFullscreenForeground("WorkerW"));
        Assert.False(TaskbarStatusPositioner.IsIgnoredFullscreenForeground("Chrome_WidgetWin_1"));
    }

    [Fact]
    public void FullscreenOnAnotherMonitor_DoesNotSuppressThisStrip()
    {
        var otherMonitor = new ScreenRect(1920, 0, 1920, 1080);
        var observation = FullscreenClassifier.Observe(
            Application(otherMonitor, maximized: false, framed: true, popup: false),
            Monitor,
            TaskbarWorkArea);
        Assert.Equal(FullscreenObservationKind.ConfirmedNotFullscreen, observation.Kind);

        var gate = new TaskbarStripVisibilityGate();
        var decision = gate.Observe(Normal(), TaskbarStripMode.Full, observation);
        Assert.Equal(TaskbarStripVisibilityAction.Show, decision.Action);
        Assert.False(gate.FullscreenSuppressed);
    }

    [Fact]
    public void SuppressedLogging_ReportsTransitionsWithoutRepeating()
    {
        var gate = Shown();
        var facts = new ForegroundWindowFacts(
            "Chrome_WidgetWin_1",
            Monitor,
            true,
            true,
            false,
            true,
            ForegroundWindowRole.Application);
        var fullscreen = FullscreenClassifier.Observe(facts, Monitor, Monitor);
        var hide = gate.Observe(Fullscreen(), TaskbarStripMode.Hidden, fullscreen);
        Assert.Contains("observation=fullscreen", hide.LogLine, StringComparison.Ordinal);
        Assert.Contains("foregroundClass=Chrome_WidgetWin_1", hide.LogLine, StringComparison.Ordinal);
        Assert.Contains("foregroundRect=0,0,1920x1080", hide.LogLine, StringComparison.Ordinal);
        Assert.Contains("monitorRect=0,0,1920x1080", hide.LogLine, StringComparison.Ordinal);
        Assert.Contains("workAreaRect=0,0,1920x1080", hide.LogLine, StringComparison.Ordinal);
        Assert.Contains("maximized=true", hide.LogLine, StringComparison.Ordinal);
        Assert.Contains("framed=false", hide.LogLine, StringComparison.Ordinal);
        Assert.Contains("suppression=true", hide.LogLine, StringComparison.Ordinal);

        Assert.Null(gate.Observe(Fullscreen(), TaskbarStripMode.Hidden, fullscreen).LogLine);
        Assert.Null(gate.Observe(Fullscreen(), TaskbarStripMode.Hidden, fullscreen).LogLine);

        var unknown = FullscreenClassifier.Observe(
            ForegroundWindowFacts.Missing(ForegroundWindowRole.ShellTaskbar),
            Monitor,
            Monitor);
        var transition = gate.Observe(Fullscreen(), TaskbarStripMode.Hidden, unknown);
        Assert.Contains("observation=unknown", transition.LogLine, StringComparison.Ordinal);
        Assert.Contains("fullscreen-exit-unconfirmed", transition.LogLine, StringComparison.Ordinal);
        Assert.Null(gate.Observe(Fullscreen(), TaskbarStripMode.Hidden, unknown).LogLine);
    }

    private static ForegroundWindowFacts Application(
        ScreenRect bounds,
        bool maximized,
        bool framed,
        bool popup) =>
        new("Chrome_WidgetWin_1", bounds, true, maximized, framed, popup, ForegroundWindowRole.Application);

    private static TaskbarStripVisibilityGate Shown()
    {
        var gate = new TaskbarStripVisibilityGate();
        var decision = Observe(gate, FullscreenObservationKind.ConfirmedNotFullscreen);
        Assert.Equal(TaskbarStripVisibilityAction.Show, decision.Action);
        return gate;
    }

    private static TaskbarStripVisibilityDecision Observe(
        TaskbarStripVisibilityGate gate,
        FullscreenObservationKind kind)
    {
        var fullscreen = kind == FullscreenObservationKind.Fullscreen;
        return gate.Observe(
            fullscreen ? Fullscreen() : Normal(),
            fullscreen ? TaskbarStripMode.Hidden : TaskbarStripMode.Full,
            new FullscreenObservation(
                kind,
                "test-sample",
                ForegroundWindowFacts.Missing(ForegroundWindowRole.Application),
                Monitor,
                TaskbarWorkArea));
    }

    private static TaskbarLayoutInput Fullscreen() => Normal() with { ExclusiveFullscreenOnMonitor = true };

    private static TaskbarLayoutInput Normal() => new(
        Monitor,
        TaskbarWorkArea,
        new ScreenRect(0, 1040, 1920, 40),
        new ScreenRect(1760, 1040, 160, 40),
        new ScreenRect(1840, 1040, 80, 40),
        new ScreenRect(0, 1040, 1360, 40),
        TaskbarEdge.Bottom,
        1,
        false,
        true,
        false);
}
