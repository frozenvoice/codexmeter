using System.Xml.Linq;
using ProMeter.Services;

namespace ProMeter.Tests;

public class WidgetInteractionTests
{
    [Fact]
    public void DragThreshold_SeparatesClickFromDrag()
    {
        Assert.True(WidgetInteraction.IsClick(0, 0, 2, 2));
        Assert.False(WidgetInteraction.IsDrag(0, 0, 2, 2));
        Assert.True(WidgetInteraction.IsDrag(0, 0, 10, 0));
        Assert.False(WidgetInteraction.IsClick(0, 0, 10, 1));
    }

    [Fact]
    public void EventSubscription_HappensOnlyOnce()
    {
        var binder = new OnceEventSubscription();
        var count = 0;
        Assert.True(binder.TrySubscribe(() => count++));
        Assert.False(binder.TrySubscribe(() => count++));
        Assert.Equal(1, count);
        Assert.Equal(1, binder.Count);
    }

    [Fact]
    public void FloatingWidgetXaml_UsesThemeResourcesAndHasNoExactQuotaPrototype()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FloatingWidget.xaml"));
        var xaml = document.ToString();
        Assert.Contains("CardBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("TextBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("MutedBrush", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProStateValue", xaml, StringComparison.Ordinal);
        Assert.Contains("OnPreviewLeftDown", xaml, StringComparison.Ordinal);
        Assert.Contains("OnMouseDown", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("31/50", xaml, StringComparison.Ordinal);
        var widgetCode = File.ReadAllText(Find("src/ProMeter/UI/FloatingWidget.xaml.cs"));
        Assert.Contains("FlyoutRequested", widgetCode, StringComparison.Ordinal);
        Assert.Contains("RefreshRequested", widgetCode, StringComparison.Ordinal);
        Assert.Contains("ContextMenuRequested", widgetCode, StringComparison.Ordinal);
        Assert.Contains("WidgetDragSession", widgetCode, StringComparison.Ordinal);
        Assert.DoesNotContain("DragMove()", widgetCode, StringComparison.Ordinal);
        Assert.Contains("MouseButton.Middle", widgetCode, StringComparison.Ordinal);
        var appCode = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("_widgetEvents.TrySubscribe", appCode, StringComparison.Ordinal);
        Assert.Contains("RefreshCodexAsync", appCode, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetConversation", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsXaml_RemovesChatControlsAndKeepsSurfaceToggles()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "SettingsWindow.xaml"));
        var xaml = document.ToString();
        Assert.DoesNotContain("RECONSTRUCTION WINDOW", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskbarStatusBox", xaml, StringComparison.Ordinal);
        Assert.Contains("TrayHint", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetOpacityBox", xaml, StringComparison.Ordinal);
        Assert.Contains("WidgetClickThroughBox", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("RESET ANCHOR", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutXaml_HasCodexRowsOnly()
    {
        var xaml = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml")).ToString();
        Assert.Contains("CodexRows", xaml, StringComparison.Ordinal);
        Assert.Contains("CodexStatusText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ProStateText", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfirmedRequestsText", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetCloseMenu_HidesAndPersistsWithoutExitingApp()
    {
        var tray = File.ReadAllText(Find("src/ProMeter/UI/TrayController.cs"));
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("CloseWidgetRequested?.Invoke()", tray);
        Assert.Contains("위젯 닫기", tray);
        Assert.Contains("_widget.ContextMenuRequested += () => _tray.ShowWidgetContextMenu()", app);
        Assert.Contains("_tray.CloseWidgetRequested += CloseWidget", app);
        var start = app.IndexOf("private void CloseWidget()", StringComparison.Ordinal);
        var close = app[start..app.IndexOf("private void ApplyWidget()", start, StringComparison.Ordinal)];
        Assert.Contains("_settings.FloatingWidgetEnabled = false", close);
        Assert.Contains("_settingsStore.Save(_settings)", close);
        Assert.Contains("ApplyWidget()", close);
        Assert.DoesNotContain("ExitApp", close);
        Assert.DoesNotContain("Shutdown", close);
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
