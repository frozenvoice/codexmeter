using System.Xml.Linq;
using ProMeter.Codex;
using ProMeter.Services;

namespace ProMeter.Tests;

public class FlyoutUxAndFailureSummaryTests
{
    [Fact]
    public void TaskbarStrip_DoesNotAssignHoverToolTip()
    {
        var source = File.ReadAllText(Find("src/ProMeter/UI/TaskbarStatusStripWindow.xaml.cs"));
        var xaml = File.ReadAllText(Find("src/ProMeter/UI/TaskbarStatusStripWindow.xaml"));
        Assert.DoesNotContain("ToolTip =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTipService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ToolTip", xaml, StringComparison.Ordinal);
        Assert.Contains("FlyoutRequested", source, StringComparison.Ordinal);
        Assert.Contains("RefreshRequested", source, StringComparison.Ordinal);
        Assert.Contains("ContextMenuRequested", source, StringComparison.Ordinal);
        Assert.Contains("ReassertTopmost", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutPinPolicy_DefaultTransientAndPinnedAlwaysOnTopSemantics()
    {
        Assert.False(AppSettings.CreateDefaults().FlyoutPinned);
        Assert.False(AppSettings.CreateDefaults().FlyoutPositionConfigured);
        Assert.True(FlyoutWindowState.ShouldCloseOnDeactivate(false, true));
        Assert.False(FlyoutWindowState.ShouldCloseOnDeactivate(true, true));
        Assert.False(FlyoutWindowState.ShouldCloseOnDeactivate(false, false));
        Assert.True(FlyoutWindowState.ShouldHideOnDeactivate(false, true));
        Assert.False(FlyoutWindowState.ShouldHideOnDeactivate(true, true));
        Assert.False(FlyoutWindowState.ShouldHideOnDeactivate(false, false));
        Assert.True(FlyoutWindowState.UseSavedPosition(true, true));
        Assert.False(FlyoutWindowState.UseSavedPosition(true, false));
        Assert.False(FlyoutWindowState.UseSavedPosition(false, true));
        Assert.True(FlyoutWindowState.RepositionNearAnchorOnUnpin);
    }

    [Fact]
    public void HeaderActions_DoNotLatchFutureDeactivation()
    {
        const bool closeOnDeactivateSetting = true;
        Assert.True(FlyoutWindowState.ShouldHideOnDeactivate(pinned: false, closeOnDeactivateSetting));
        Assert.False(FlyoutWindowState.ShouldHideOnDeactivate(pinned: true, closeOnDeactivateSetting));
        Assert.True(FlyoutWindowState.ShouldHideOnDeactivate(pinned: false, closeOnDeactivateSetting));

        var xaml = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml"));
        var code = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        Assert.DoesNotContain("_suppressDeactivateClose", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnHeaderButtonPreviewMouseDown", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("OnHeaderButtonPreviewMouseDown", code, StringComparison.Ordinal);
        Assert.Contains("OnRefreshAllClick", code, StringComparison.Ordinal);
        Assert.Contains("OnPinClick", code, StringComparison.Ordinal);
        Assert.Contains("OnCloseClick", code, StringComparison.Ordinal);
        Assert.Contains("DragMove()", code, StringComparison.Ordinal);
        Assert.Contains("if (CloseOnDeactivate)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void OffScreenSavedPosition_IsRecoveredToWorkArea()
    {
        var work = new ScreenRect(0, 0, 1920, 1040);
        var clamped = FlyoutPlacement.ClampToWorkArea(5000, -400, 320, 400, work);
        Assert.InRange(clamped.Left, 8, 1920 - 328);
        Assert.InRange(clamped.Top, 8, 1040 - 408);
        var selected = FlyoutPlacement.SelectWorkArea(
            5000,
            5000,
            320,
            400,
            [work, new ScreenRect(1920, 0, 1280, 1024)]);
        Assert.Equal(work, selected);
        var onSecond = FlyoutPlacement.SelectWorkArea(
            2000,
            40,
            320,
            400,
            [work, new ScreenRect(1920, 0, 1280, 1024)]);
        Assert.Equal(1920, onSecond.X);
    }

    [Fact]
    public void FlyoutXaml_HasPinCloseStripAndNoDefaultProgressBar()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var xaml = document.ToString();
        Assert.Contains("x:Name=\"TitleText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PinButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CloseFlyoutButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SyncProgressStrip\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SyncProgressTranslate\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Height=\"3\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AccentBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProgressBar", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("📌", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("📍", xaml, StringComparison.Ordinal);
        var strip = document.Descendants(ns + "Grid")
            .Single(element => (string?)element.Attribute(x + "Name") == "SyncProgressStrip");
        Assert.Equal("Collapsed", (string?)strip.Attribute("Visibility"));
        Assert.Equal("3", (string?)strip.Attribute("Height"));
        var spinner = document.Descendants(ns + "Viewbox")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshSpinner");
        Assert.Equal("Collapsed", (string?)spinner.Attribute("Visibility"));
        var close = document.Descendants(ns + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "CloseFlyoutButton");
        Assert.Equal("OnCloseClick", (string?)close.Attribute("Click"));
        var pin = document.Descendants(ns + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "PinButton");
        Assert.Equal("OnPinClick", (string?)pin.Attribute("Click"));
        Assert.Contains("OnHeaderMouseLeftButtonDown", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutCode_CloseHidesWithoutExit_AndDragIgnoresButtons()
    {
        var code = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("private void OnCloseClick", code, StringComparison.Ordinal);
        var close = code[code.IndexOf("private void OnCloseClick", StringComparison.Ordinal)..];
        close = close[..close.IndexOf("private void OnHeaderMouseLeftButtonDown", StringComparison.Ordinal)];
        Assert.Contains("Hide();", close, StringComparison.Ordinal);
        Assert.DoesNotContain("Shutdown", close, StringComparison.Ordinal);
        Assert.DoesNotContain("Close();", close, StringComparison.Ordinal);
        Assert.DoesNotContain("Pinned = false", close, StringComparison.Ordinal);
        Assert.Contains("DragMove()", code, StringComparison.Ordinal);
        Assert.Contains("HeaderSourceIsInteractive", code, StringComparison.Ordinal);
        Assert.Contains("if (!Pinned", code, StringComparison.Ordinal);
        Assert.DoesNotContain("_suppressDeactivateClose", code, StringComparison.Ordinal);
        Assert.DoesNotContain("OnHeaderButtonPreviewMouseDown", code, StringComparison.Ordinal);
        var deactivate = code[code.IndexOf("private void OnDeactivated", StringComparison.Ordinal)..];
        deactivate = deactivate[..deactivate.IndexOf("private void OnPreviewKeyDown", StringComparison.Ordinal)];
        Assert.Contains("if (CloseOnDeactivate)", deactivate, StringComparison.Ordinal);
        Assert.Contains("Hide();", deactivate, StringComparison.Ordinal);
        Assert.DoesNotContain("suppress", deactivate, StringComparison.OrdinalIgnoreCase);
        var refresh = code[code.IndexOf("private void OnRefreshAllClick", StringComparison.Ordinal)..];
        refresh = refresh[..refresh.IndexOf("private void OnPinClick", StringComparison.Ordinal)];
        Assert.Contains("SyncRequested", refresh, StringComparison.Ordinal);
        Assert.DoesNotContain("CloseOnDeactivate", refresh, StringComparison.Ordinal);
        var pin = code[code.IndexOf("private void OnPinClick", StringComparison.Ordinal)..];
        pin = pin[..pin.IndexOf("private void OnCloseClick", StringComparison.Ordinal)];
        Assert.Contains("ShouldCloseOnDeactivate(Pinned, _closeOnDeactivateSetting)", pin, StringComparison.Ordinal);
        Assert.DoesNotContain("suppress", pin, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EnsureProgressStripStoryboard", code, StringComparison.Ordinal);
        Assert.Contains("SyncProgressStrip.Visibility", code, StringComparison.Ordinal);
        Assert.Contains("StripDurationSeconds", code, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior.Forever", code, StringComparison.Ordinal);
        Assert.Contains("FlyoutPinned", app, StringComparison.Ordinal);
        Assert.Contains("FlyoutPositionConfigured", app, StringComparison.Ordinal);
        Assert.Contains("UseSavedPosition", app, StringComparison.Ordinal);
        var settingsUi = File.ReadAllText(Find("src/ProMeter/UI/SettingsWindow.xaml"));
        Assert.DoesNotContain("FlyoutLeft", settingsUi, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutPinned", settingsUi, StringComparison.Ordinal);
        var settingsApp = File.ReadAllText(Find("src/ProMeter.Core/Services/SettingsApplication.cs"));
        Assert.DoesNotContain("FlyoutLeft", settingsApp, StringComparison.Ordinal);
        Assert.DoesNotContain("FlyoutPinned", settingsApp, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageWindow_ShowsStructuredFailureLines()
    {
        var xaml = File.ReadAllText(Find("src/ProMeter/UI/CoverageWindow.xaml"));
        var code = File.ReadAllText(Find("src/ProMeter/UI/CoverageWindow.xaml.cs"));
        Assert.Contains("FailurePanel", xaml, StringComparison.Ordinal);
        Assert.Contains("CoverageFailureDetailLines", code, StringComparison.Ordinal);
        Assert.Contains("CoverageCompactLabel", code, StringComparison.Ordinal);
        Assert.DoesNotContain("conversation_id", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnglishFailureSummary_OmitsTechnicalNoise()
    {
        var coverage = new CoverageInfo
        {
            FailedConversations = 3,
            ConversationIncomplete = true,
            NormalIndexState = CollectionState.Complete,
            ArchivedIndexState = CollectionState.Complete,
            ProjectsIndexState = CollectionState.Complete,
            FailureSummary =
            {
                FailedThisSyncCount = 3,
                BodyTimeoutCount = 1,
                SchemaMismatchCount = 2
            }
        };
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("3 conversations not read", DisplayFormatting.CoverageCompactLabel(coverage));
            var details = string.Join('\n', DisplayFormatting.CoverageFailureDetailLines(coverage));
            Assert.Contains("Read timeout    1", details, StringComparison.Ordinal);
            Assert.Contains("Response format mismatch    2", details, StringComparison.Ordinal);
            Assert.Contains("Failed this sync    3", details, StringComparison.Ordinal);
            Assert.Contains("lower bound", details, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaMismatch", details, StringComparison.Ordinal);
            Assert.DoesNotContain("BodyTimeout", details, StringComparison.Ordinal);
            Assert.DoesNotContain("generation", details, StringComparison.Ordinal);
            Assert.DoesNotContain("HTTP status=0", details, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void GlobalIndexFailure_DoesNotInventConversationCounts()
    {
        var coverage = new CoverageInfo
        {
            IndexIncomplete = true,
            NormalIndexState = CollectionState.Failed,
            FailedConversations = 0
        };
        Assert.False(coverage.FailureSummary.HasConversationFailures);
        Assert.Empty(DisplayFormatting.CoverageFailureDetailLines(coverage));
        Assert.Equal(UiText.Partial, DisplayFormatting.CoverageCompactLabel(coverage));
        Assert.DoesNotContain("conversations not read", DisplayFormatting.CoverageCompactLabel(coverage), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RefreshIndicator_IdleActiveHiddenAndResume()
    {
        var controller = new RefreshIndicatorController();
        Assert.False(controller.IsAnimating);
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Reset());
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Apply(false));
        Assert.False(controller.IsAnimating);
        Assert.Equal(0, controller.Angle);
        var active = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
        Assert.True(active.Active);
        Assert.Equal(UiText.RefreshAllProgress, active.ProgressText);
        var idle = CombinedRefreshCoordinator.Present(false, false);
        Assert.False(idle.Active);
        Assert.True(idle.ShowNormalStatus);
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
}
