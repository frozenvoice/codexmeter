using System.Globalization;
using ProMeter.Codex;
using ProMeter.Models;
using ProMeter.Services;

namespace ProMeter.Tests;

public class TaskbarStatusLayoutTests
{
    [Fact]
    public void BottomTaskbar_PlacesFullModeLeftOfNotify()
    {
        var result = TaskbarStatusPositioner.Place(Bottom(gap: 400));
        Assert.Equal(TaskbarStripMode.Full, result.Mode);
        Assert.True(result.Visible);
        Assert.False(result.OverlapsNotify);
        Assert.False(result.OverlapsClock);
        Assert.True(result.Bounds.Right <= 1760);
        Assert.True(result.Bounds.Y >= 1032);
        Assert.True(result.Bounds.Bottom <= 1080);
    }

    [Fact]
    public void CompactAndUltra_FallBackWhenGapShrinks()
    {
        Assert.Equal(TaskbarStripMode.Compact, TaskbarStatusPositioner.Place(Bottom(gap: 130)).Mode);
        Assert.Equal(TaskbarStripMode.UltraCompact, TaskbarStatusPositioner.Place(Bottom(gap: 100)).Mode);
        var above = TaskbarStatusPositioner.Place(Bottom(gap: 20));
        Assert.Equal(TaskbarStripMode.AboveTaskbar, above.Mode);
        Assert.True(above.Bounds.Bottom <= 1032);
    }

    [Fact]
    public void TopLeftRight_StayOffClock()
    {
        var top = TaskbarStatusPositioner.Place(new TaskbarLayoutInput(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 40, 1920, 1040),
            new ScreenRect(0, 0, 1920, 40),
            new ScreenRect(1760, 0, 160, 40),
            new ScreenRect(1840, 0, 80, 40),
            new ScreenRect(0, 0, 1400, 40),
            TaskbarEdge.Top,
            1,
            false,
            true,
            false));
        Assert.True(top.Bounds.Right <= 1760);
        Assert.True(top.Visible);

        var left = TaskbarStatusPositioner.Place(new TaskbarLayoutInput(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(72, 0, 1848, 1080),
            new ScreenRect(0, 0, 72, 1080),
            new ScreenRect(0, 980, 72, 100),
            new ScreenRect(0, 1020, 72, 60),
            new ScreenRect(0, 0, 72, 800),
            TaskbarEdge.Left,
            1,
            false,
            true,
            false));
        Assert.True(left.Visible);
        Assert.True(left.Bounds.Bottom <= 980 || left.Mode == TaskbarStripMode.AboveTaskbar);

        var right = TaskbarStatusPositioner.Place(new TaskbarLayoutInput(
            new ScreenRect(0, 0, 1920, 1080),
            new ScreenRect(0, 0, 1848, 1080),
            new ScreenRect(1848, 0, 72, 1080),
            new ScreenRect(1848, 980, 72, 100),
            new ScreenRect(1848, 1020, 72, 60),
            new ScreenRect(1848, 0, 72, 800),
            TaskbarEdge.Right,
            1,
            false,
            true,
            false));
        Assert.True(right.Visible);
        Assert.False(right.OverlapsClock);
    }

    [Fact]
    public void HighDpi_ConvertsPhysicalBoundsToDip()
    {
        var placed = TaskbarStatusPositioner.Place(Bottom(gap: 400, scale: 2, width: 3840, height: 2160, taskbarHeight: 56));
        var dip = TaskbarStatusPositioner.ToDip(placed.Bounds, 2);
        Assert.Equal(placed.Bounds.Width / 2d, dip.Width);
        Assert.True(dip.Height <= 30);
    }

    [Fact]
    public void ExclusiveFullscreenHides_MaximizedDoesNot()
    {
        var monitor = new ScreenRect(0, 0, 1920, 1080);
        var work = new ScreenRect(0, 0, 1920, 1040);
        Assert.True(TaskbarStatusPositioner.IsExclusiveFullscreen(monitor, monitor, work, taskbarVisible: false));
        Assert.False(TaskbarStatusPositioner.IsExclusiveFullscreen(work, monitor, work, taskbarVisible: true));
        Assert.False(TaskbarStatusPositioner.ShouldShow(true, true, false));
        Assert.True(TaskbarStatusPositioner.ShouldShow(true, false, true));
        Assert.False(TaskbarStatusPositioner.ShouldShow(false, false, true));
    }

    [Fact]
    public void HiddenWhenTaskbarAutoHideAndInvisible()
    {
        var input = Bottom(gap: 400) with { TaskbarVisible = false, TaskbarAutoHide = true };
        Assert.Equal(TaskbarStripMode.Hidden, TaskbarStatusPositioner.Place(input).Mode);
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom)]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void AutoHide_HidesWhenPeekOrOffScreen(TaskbarEdge edge)
    {
        var peek = AutoHide(edge, revealed: false, peek: true);
        var offScreen = AutoHide(edge, revealed: false, peek: false);
        Assert.False(TaskbarStatusPositioner.ShouldShow(peek));
        Assert.False(TaskbarStatusPositioner.ShouldShow(offScreen));
        Assert.Equal(TaskbarStripMode.Hidden, TaskbarStatusPositioner.Place(peek).Mode);
        Assert.Equal(TaskbarStripMode.Hidden, TaskbarStatusPositioner.Place(offScreen).Mode);
        Assert.True(TaskbarVisibilityDetector.ExposedThickness(peek.Taskbar, peek.Monitor, edge)
                    < TaskbarVisibilityDetector.RevealedThicknessPx);
    }

    [Theory]
    [InlineData(TaskbarEdge.Bottom)]
    [InlineData(TaskbarEdge.Top)]
    [InlineData(TaskbarEdge.Left)]
    [InlineData(TaskbarEdge.Right)]
    public void AutoHide_ShowsWhenTaskbarIsRevealed(TaskbarEdge edge)
    {
        var revealed = AutoHide(edge, revealed: true);
        Assert.True(TaskbarVisibilityDetector.IsRevealed(true, revealed.Taskbar, revealed.Monitor, edge));
        Assert.True(TaskbarStatusPositioner.ShouldShow(revealed));
        Assert.True(TaskbarStatusPositioner.Place(revealed).Visible);
        Assert.NotEqual(TaskbarStripMode.Hidden, TaskbarStatusPositioner.Place(revealed).Mode);
    }

    [Fact]
    public void AutoHide_ExplorerRestartUsesFreshRectangles()
    {
        var hidden = AutoHide(TaskbarEdge.Bottom, revealed: false);
        var afterRestart = AutoHide(TaskbarEdge.Bottom, revealed: true);
        Assert.False(TaskbarStatusPositioner.Place(hidden).Visible);
        Assert.True(TaskbarStatusPositioner.Place(afterRestart).Visible);
        Assert.NotEqual(hidden.Taskbar, afterRestart.Taskbar);
    }

    [Fact]
    public void AutoHide_ExclusiveFullscreenHidesEvenWhenRevealed()
    {
        var revealed = AutoHide(TaskbarEdge.Bottom, revealed: true) with { ExclusiveFullscreenOnMonitor = true };
        Assert.False(TaskbarStatusPositioner.ShouldShow(revealed));
        Assert.Equal(TaskbarStripMode.Hidden, TaskbarStatusPositioner.Place(revealed).Mode);
    }

    [Fact]
    public void NonAutoHide_KeepsNormalVisibility()
    {
        Assert.True(TaskbarStatusPositioner.Place(Bottom(gap: 400)).Visible);
        var missing = Bottom(gap: 400) with { TaskbarVisible = false };
        Assert.False(TaskbarStatusPositioner.ShouldShow(missing));
    }

    [Fact]
    public void NonAutoHide_DoesNotRequireRevealedThickness()
    {
        var thin = Bottom(gap: 400, taskbarHeight: 8);
        Assert.True(thin.Taskbar.Height < TaskbarVisibilityDetector.RevealedThicknessPx);
        Assert.True(TaskbarVisibilityDetector.IsRevealed(false, thin.Taskbar, thin.Monitor, thin.Edge));
        Assert.True(TaskbarStatusPositioner.ShouldShow(thin));
        Assert.True(TaskbarStatusPositioner.Place(thin).Visible);
        Assert.NotEqual(TaskbarStripMode.Hidden, TaskbarStatusPositioner.Place(thin).Mode);
    }

    [Fact]
    public void VisibilityGate_HidesOnlyAfterConsecutiveGenuineHiddenSamples()
    {
        var gate = new TaskbarStripVisibilityGate();
        var visible = Bottom(gap: 400);
        var shown = gate.Observe(visible, TaskbarStripMode.Full);
        Assert.Equal(TaskbarStripVisibilityAction.Show, shown.Action);
        Assert.True(shown.OverlayVisible);
        Assert.Contains("taskbar strip shown", shown.LogLine, StringComparison.Ordinal);

        var invalid = visible with { Taskbar = default };
        var retained = gate.Observe(invalid, TaskbarStripMode.Hidden);
        Assert.Equal(TaskbarStripVisibilityAction.Retain, retained.Action);
        Assert.True(retained.OverlayVisible);
        Assert.Contains("transient-invalid-geometry", retained.LogLine, StringComparison.Ordinal);

        var recovered = gate.Observe(visible, TaskbarStripMode.Full);
        Assert.True(recovered.OverlayVisible);

        var autoHidden = AutoHide(TaskbarEdge.Bottom, revealed: false);
        Assert.Equal(TaskbarStripVisibilityAction.Retain, gate.Observe(autoHidden, TaskbarStripMode.Hidden).Action);
        Assert.True(gate.OverlayVisible);
        Assert.Equal(TaskbarStripVisibilityAction.Retain, gate.Observe(autoHidden, TaskbarStripMode.Hidden).Action);
        Assert.True(gate.OverlayVisible);
        var hidden = gate.Observe(autoHidden, TaskbarStripMode.Hidden);
        Assert.Equal(TaskbarStripVisibilityAction.Hide, hidden.Action);
        Assert.False(hidden.OverlayVisible);
        Assert.Contains("auto-hide-collapsed", hidden.LogLine, StringComparison.Ordinal);

        var shownAgain = gate.Observe(visible, TaskbarStripMode.Full);
        Assert.Equal(TaskbarStripVisibilityAction.Show, shownAgain.Action);

        var pending = AutoHide(TaskbarEdge.Bottom, revealed: false);
        gate.Observe(pending, TaskbarStripMode.Hidden);
        var reset = gate.Observe(visible, TaskbarStripMode.Full);
        Assert.True(reset.OverlayVisible);
        Assert.Equal(TaskbarStripVisibilityAction.Retain, reset.Action);
    }

    [Fact]
    public void VisibilityGate_HidesImmediatelyForExclusiveFullscreen()
    {
        var gate = new TaskbarStripVisibilityGate();
        gate.Observe(Bottom(gap: 400), TaskbarStripMode.Full);
        var fullscreen = Bottom(gap: 400) with { ExclusiveFullscreenOnMonitor = true, TaskbarVisible = false };
        var decision = gate.Observe(fullscreen, TaskbarStripMode.Hidden);
        Assert.Equal(TaskbarStripVisibilityAction.Hide, decision.Action);
        Assert.False(decision.OverlayVisible);
        Assert.Contains("exclusive-fullscreen", decision.LogLine, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexStatus_DoesNotAffectTaskbarVisibility()
    {
        var input = Bottom(gap: 400);
        Assert.True(TaskbarStatusPositioner.Place(input).Visible);
        var gpt = new QuotaSnapshot { Used = 31, ReconstructedUsed = 31 };
        var mismatch = CodexQuotaSnapshot.Empty(CodexQuotaStatus.ProtocolMismatch, "protocol-error");
        Assert.Contains("C?", TaskbarStatusFormatter.Format(gpt, mismatch, TaskbarStripMode.Full), StringComparison.Ordinal);
        Assert.True(TaskbarStatusPositioner.ShouldShow(input));
        var source = File.ReadAllText(FindStripWindow());
        var bind = source[source.IndexOf("public void Bind(", StringComparison.Ordinal)..source.IndexOf("public void ApplyThemeResources", StringComparison.Ordinal)];
        Assert.Contains("ApplyText();", bind, StringComparison.Ordinal);
        Assert.DoesNotContain("Reposition();", bind, StringComparison.Ordinal);
        Assert.Contains("ReassertTopmost", source, StringComparison.Ordinal);
        Assert.Contains("TaskbarTopmostPlacement.ShouldReassert", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFocus(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetParent", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TopmostReassert_UsesSafeNoActivateFlags()
    {
        Assert.Equal(new IntPtr(-1), TaskbarTopmostPlacement.HwndTopmost);
        Assert.True(TaskbarTopmostPlacement.HasRequiredSafetyFlags);
        Assert.Equal(0u, TaskbarTopmostPlacement.Flags & TaskbarTopmostPlacement.SwpNoZOrder);
        Assert.Equal(TaskbarTopmostPlacement.SwpNoActivate, TaskbarTopmostPlacement.Flags & TaskbarTopmostPlacement.SwpNoActivate);
        Assert.True(TaskbarTopmostPlacement.ShouldReassert(true));
        Assert.False(TaskbarTopmostPlacement.ShouldReassert(false));
        var win32 = File.ReadAllText(Find("src/ProMeter/UI/TaskbarWin32.cs"));
        Assert.Contains("ReassertTopmostNoActivate", win32, StringComparison.Ordinal);
        Assert.Contains("SetLastError = true", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFocus", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetParent", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowsHook", win32, StringComparison.Ordinal);
        var strip = File.ReadAllText(FindStripWindow());
        var reposition = strip[strip.IndexOf("public void Reposition()", StringComparison.Ordinal)..];
        Assert.Contains("ReassertTopmost(decision.OverlayVisible)", reposition, StringComparison.Ordinal);
        Assert.Contains("if (!decision.OverlayVisible)", reposition, StringComparison.Ordinal);
        Assert.DoesNotContain("SetForegroundWindow", strip, StringComparison.Ordinal);
    }

    private static string FindStripWindow() => Find("src/ProMeter/UI/TaskbarStatusStripWindow.xaml.cs");

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

    [Fact]
    public void DisplayAndExplorerSignals_AreDebounced()
    {
        var debounce = new LayoutSignalDebouncer();
        var now = DateTimeOffset.Parse("2026-09-05T01:00:00Z");
        Assert.True(debounce.ShouldHandle(now, TimeSpan.FromMilliseconds(200)));
        Assert.False(debounce.ShouldHandle(now.AddMilliseconds(50), TimeSpan.FromMilliseconds(200)));
        Assert.True(debounce.ShouldHandle(now.AddMilliseconds(250), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Clicks_MapToFlyoutRefreshAndMenu()
    {
        Assert.Equal(TaskbarStripAction.ToggleFlyout, TaskbarStripInteraction.FromButton(TaskbarStripInteraction.Left));
        Assert.Equal(TaskbarStripAction.CombinedRefresh, TaskbarStripInteraction.FromButton(TaskbarStripInteraction.Middle));
        Assert.Equal(TaskbarStripAction.ContextMenu, TaskbarStripInteraction.FromButton(TaskbarStripInteraction.Right));
    }

    [Fact]
    public void Formatting_CoversUnavailableStaleRefreshingAndModes()
    {
        var gpt = new QuotaSnapshot { Used = 31, Limit = 50, ReconstructedUsed = 31 };
        var weekly = new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            null,
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            null,
            null,
            null,
            [new CodexQuotaWindow(null, 42, 10080, null, CodexWindowKind.Weekly)],
            null);
        Assert.Equal("P? · C 42%", TaskbarStatusFormatter.Format(gpt, weekly, TaskbarStripMode.Full));
        Assert.Equal("P? C42%", TaskbarStatusFormatter.Format(gpt, weekly, TaskbarStripMode.Compact));
        Assert.Equal("P? C42", TaskbarStatusFormatter.Format(gpt, weekly, TaskbarStripMode.UltraCompact));
        Assert.DoesNotContain("31/50", TaskbarStatusFormatter.Format(gpt, weekly, TaskbarStripMode.Full), StringComparison.Ordinal);

        var missing = new QuotaSnapshot { DisplayUsageUnavailable = true, Limit = 50 };
        var none = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
        Assert.Contains("P?", TaskbarStatusFormatter.Format(missing, none, TaskbarStripMode.Compact));
        Assert.Contains("C?", TaskbarStatusFormatter.Format(missing, none, TaskbarStripMode.Compact));

        var stale = weekly with { Status = CodexQuotaStatus.Stale };
        Assert.Contains("!", TaskbarStatusFormatter.Format(gpt, stale, TaskbarStripMode.Compact));
        Assert.Contains("C …", TaskbarStatusFormatter.Format(gpt, CodexQuotaSnapshot.Empty(CodexQuotaStatus.Refreshing), TaskbarStripMode.Full));
    }

    [Fact]
    public void ProTokens_MarkStaleWithTildeAndKeepUnknownPlain()
    {
        var reset = new DateTimeOffset(2026, 9, 6, 5, 20, 0, TimeSpan.Zero);
        var time = reset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var weekly = new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            null,
            DateTimeOffset.Now,
            DateTimeOffset.Now,
            null,
            null,
            null,
            [new CodexQuotaWindow(null, 42, 10080, null, CodexWindowKind.Weekly)],
            null);

        var restrictedFresh = Restricted(reset, stale: false);
        Assert.Equal($"P! {time}", TaskbarStatusFormatter.ChatGptToken(restrictedFresh, TaskbarStripMode.Full));
        Assert.Equal($"P!{time}", TaskbarStatusFormatter.ChatGptToken(restrictedFresh, TaskbarStripMode.Compact));
        Assert.Equal("P!", TaskbarStatusFormatter.ChatGptToken(restrictedFresh, TaskbarStripMode.UltraCompact));
        Assert.Equal($"P! {time} · C 42%", TaskbarStatusFormatter.Format(restrictedFresh, weekly, TaskbarStripMode.Full));
        Assert.Equal($"P!{time} C42%", TaskbarStatusFormatter.Format(restrictedFresh, weekly, TaskbarStripMode.Compact));
        Assert.Equal("P! C42", TaskbarStatusFormatter.Format(restrictedFresh, weekly, TaskbarStripMode.UltraCompact));

        var restrictedStale = Restricted(reset, stale: true);
        Assert.Equal($"P! {time}~", TaskbarStatusFormatter.ChatGptToken(restrictedStale, TaskbarStripMode.Full));
        Assert.Equal($"P!{time}~", TaskbarStatusFormatter.ChatGptToken(restrictedStale, TaskbarStripMode.Compact));
        Assert.Equal("P!~", TaskbarStatusFormatter.ChatGptToken(restrictedStale, TaskbarStripMode.UltraCompact));
        Assert.Equal($"P! {time}~ · C 42%", TaskbarStatusFormatter.Format(restrictedStale, weekly, TaskbarStripMode.Full));
        Assert.Equal($"P!{time}~ C42%", TaskbarStatusFormatter.Format(restrictedStale, weekly, TaskbarStripMode.Compact));
        Assert.Equal("P!~ C42", TaskbarStatusFormatter.Format(restrictedStale, weekly, TaskbarStripMode.UltraCompact));
        Assert.Contains(UiText.Stale, TaskbarStatusFormatter.Tooltip(restrictedStale, weekly), StringComparison.Ordinal);
        Assert.DoesNotContain("P?~", TaskbarStatusFormatter.ChatGptToken(restrictedStale, TaskbarStripMode.Full), StringComparison.Ordinal);

        var openFresh = Observed(stale: false);
        Assert.Equal("P OK", TaskbarStatusFormatter.ChatGptToken(openFresh, TaskbarStripMode.Full));
        Assert.Equal("POK", TaskbarStatusFormatter.ChatGptToken(openFresh, TaskbarStripMode.Compact));
        Assert.Equal("POK", TaskbarStatusFormatter.ChatGptToken(openFresh, TaskbarStripMode.UltraCompact));
        Assert.Equal("P OK · C 42%", TaskbarStatusFormatter.Format(openFresh, weekly, TaskbarStripMode.Full));

        var openStale = Observed(stale: true);
        Assert.Equal("P OK~", TaskbarStatusFormatter.ChatGptToken(openStale, TaskbarStripMode.Full));
        Assert.Equal("POK~", TaskbarStatusFormatter.ChatGptToken(openStale, TaskbarStripMode.Compact));
        Assert.Equal("POK~", TaskbarStatusFormatter.ChatGptToken(openStale, TaskbarStripMode.UltraCompact));
        Assert.Equal("P OK~ · C 42%", TaskbarStatusFormatter.Format(openStale, weekly, TaskbarStripMode.Full));
        Assert.Contains(UiText.Stale, TaskbarStatusFormatter.Tooltip(openStale, weekly), StringComparison.Ordinal);

        var unknown = new QuotaSnapshot { Used = 31, Limit = 50, ReconstructedUsed = 31 };
        var unknownStale = new QuotaSnapshot
        {
            Used = 31,
            Limit = 50,
            ReconstructedUsed = 31,
            ProServerStatus = new ProServerStatus
            {
                ServerObserved = true,
                RestrictionState = ProRestrictionState.Unknown,
                Stale = true
            }
        };
        Assert.Equal("P?", TaskbarStatusFormatter.ChatGptToken(unknown, TaskbarStripMode.Full));
        Assert.Equal("P?", TaskbarStatusFormatter.ChatGptToken(unknownStale, TaskbarStripMode.Full));
        Assert.Equal("P? · C 42%", TaskbarStatusFormatter.Format(unknown, weekly, TaskbarStripMode.Full));
    }

    private static QuotaSnapshot Restricted(DateTimeOffset reset, bool stale) => new()
    {
        Used = 31,
        Limit = 50,
        ReconstructedUsed = 31,
        ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.CorrelatedRestriction,
            ResetAt = reset,
            ResetConfidence = ServerResetConfidence.Server,
            Stale = stale
        }
    };

    private static QuotaSnapshot Observed(bool stale) => new()
    {
        Used = 31,
        Limit = 50,
        ReconstructedUsed = 31,
        ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.NoCorrelatedRestrictionObserved,
            Stale = stale
        }
    };

    [Fact]
    public void Tooltips_ExistInKoreanAndEnglish()
    {
        var gpt = new QuotaSnapshot { Used = 31, Limit = 50, ReconstructedUsed = 31 };
        var weekly = new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            null,
            new DateTimeOffset(2026, 9, 5, 1, 15, 0, TimeSpan.Zero),
            DateTimeOffset.Now,
            null,
            null,
            null,
            [new CodexQuotaWindow(null, 42, 10080, null, CodexWindowKind.Weekly)],
            null);
        UiText.SetLanguage(UiLanguage.English);
        var english = TaskbarStatusFormatter.Tooltip(gpt, weekly);
        Assert.Contains("GPT Pro:", english, StringComparison.Ordinal);
        Assert.Contains("31+", english, StringComparison.Ordinal);
        Assert.DoesNotContain("31/50", english, StringComparison.Ordinal);
        Assert.Contains("weekly", english, StringComparison.OrdinalIgnoreCase);
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = TaskbarStatusFormatter.Tooltip(gpt, weekly);
            Assert.Contains("GPT Pro:", korean, StringComparison.Ordinal);
            Assert.Contains("31+", korean, StringComparison.Ordinal);
            Assert.Contains("주간", korean, StringComparison.Ordinal);
            Assert.Contains("마지막 확인", korean, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void FlyoutPlacement_StaysInWorkArea()
    {
        var (left, top) = FlyoutPlacement.PlaceNear(
            new ScreenRect(1700, 1050, 120, 26),
            TaskbarEdge.Bottom,
            320,
            400,
            new ScreenRect(0, 0, 1920, 1040));
        Assert.InRange(left, 8, 1920 - 328);
        Assert.InRange(top, 8, 1040 - 408);
    }

    [Fact]
    public void WidthDecision_UsesAvailableGap()
    {
        Assert.Equal(TaskbarStripMode.Full, TaskbarStatusPositioner.ChooseMode(200, 1));
        Assert.Equal(TaskbarStripMode.Compact, TaskbarStatusPositioner.ChooseMode(120, 1));
        Assert.Equal(TaskbarStripMode.UltraCompact, TaskbarStatusPositioner.ChooseMode(90, 1));
        Assert.Equal(TaskbarStripMode.AboveTaskbar, TaskbarStatusPositioner.ChooseMode(20, 1));
    }

    private static TaskbarLayoutInput AutoHide(TaskbarEdge edge, bool revealed, bool peek = true)
    {
        const int width = 1920;
        const int height = 1080;
        const int full = 48;
        var thickness = revealed ? full : peek ? TaskbarVisibilityDetector.HiddenPeekThicknessPx : full;
        ScreenRect taskbar = edge switch
        {
            TaskbarEdge.Top => revealed
                ? new ScreenRect(0, 0, width, full)
                : peek
                    ? new ScreenRect(0, 0, width, thickness)
                    : new ScreenRect(0, -full, width, full),
            TaskbarEdge.Left => revealed
                ? new ScreenRect(0, 0, full, height)
                : peek
                    ? new ScreenRect(0, 0, thickness, height)
                    : new ScreenRect(-full, 0, full, height),
            TaskbarEdge.Right => revealed
                ? new ScreenRect(width - full, 0, full, height)
                : peek
                    ? new ScreenRect(width - thickness, 0, thickness, height)
                    : new ScreenRect(width, 0, full, height),
            _ => revealed
                ? new ScreenRect(0, height - full, width, full)
                : peek
                    ? new ScreenRect(0, height - thickness, width, thickness)
                    : new ScreenRect(0, height, width, full)
        };
        var notify = edge switch
        {
            TaskbarEdge.Left => new ScreenRect(taskbar.X, height - 100, taskbar.Width, 100),
            TaskbarEdge.Right => new ScreenRect(taskbar.X, height - 100, taskbar.Width, 100),
            _ => new ScreenRect(width - 160, taskbar.Y, 160, taskbar.Height)
        };
        return new TaskbarLayoutInput(
            new ScreenRect(0, 0, width, height),
            new ScreenRect(0, 0, width, height),
            taskbar,
            notify,
            notify,
            taskbar,
            edge,
            1,
            true,
            true,
            false);
    }

    private static TaskbarLayoutInput Bottom(int gap, double scale = 1, int width = 1920, int height = 1080, int taskbarHeight = 48)
    {
        var notifyWidth = 160;
        var taskListWidth = width - notifyWidth - gap;
        return new TaskbarLayoutInput(
            new ScreenRect(0, 0, width, height),
            new ScreenRect(0, 0, width, height - taskbarHeight),
            new ScreenRect(0, height - taskbarHeight, width, taskbarHeight),
            new ScreenRect(width - notifyWidth, height - taskbarHeight, notifyWidth, taskbarHeight),
            new ScreenRect(width - 80, height - taskbarHeight, 80, taskbarHeight),
            new ScreenRect(0, height - taskbarHeight, taskListWidth, taskbarHeight),
            TaskbarEdge.Bottom,
            scale,
            false,
            true,
            false);
    }
}
