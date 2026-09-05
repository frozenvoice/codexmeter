using System.Xml.Linq;
using ProMeter.Codex;
using ProMeter.Models;
using ProMeter.Services;

namespace ProMeter.Tests;

public class FlyoutRefreshAndLocalizationTests
{
    [Fact]
    public void RefreshButton_ExistsWithLocalizedNameAndThemeBrushes()
    {
        var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "FlyoutWindow.xaml"));
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var button = document.Descendants(ns + "Button")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshAllButton");
        Assert.Equal("OnRefreshAllClick", (string?)button.Attribute("Click"));
        Assert.Contains("Refresh all", (string?)button.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"))
            ?? (string?)button.Attribute("ToolTip"), StringComparison.OrdinalIgnoreCase);
        var xaml = button.ToString();
        Assert.Contains("TextBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("GhostBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("AccentBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("DisabledBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", document.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"Collapsed\"", button.ToString(), StringComparison.Ordinal);
        Assert.Contains("CODEX", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("CodexRows", document.ToString(), StringComparison.Ordinal);
        Assert.Contains("RefreshProgressText", document.ToString(), StringComparison.Ordinal);
        var icon = document.Descendants(ns + "TextBlock")
            .Single(element => (string?)element.Attribute(x + "Name") == "RefreshAllIcon");
        Assert.Equal("↻", (string?)icon.Attribute("Text") ?? icon.Value.Trim());
        Assert.Equal("0.5,0.5", (string?)icon.Attribute("RenderTransformOrigin"));
        Assert.Contains("RotateTransform", icon.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("IsMouseOver", icon.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Storyboard", button.ToString(), StringComparison.Ordinal);
        var flyoutCode = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("RefreshIndicatorController", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("RepeatBehavior.Forever", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("EasingFunction", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("_suppressDeactivateClose = true", flyoutCode, StringComparison.Ordinal);
    }

    [Fact]
    public void RefreshPresentation_DisablesDuringCombinedManual_AndKeepsProviderText()
    {
        var idle = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(idle.Enabled);
        Assert.False(idle.Active);
        Assert.Equal("", idle.ProgressText);
        Assert.True(idle.ShowNormalStatus);
        Assert.False(idle.ShowRefreshProgress);

        var autoCodex = CombinedRefreshCoordinator.Present(false, true);
        Assert.True(autoCodex.Enabled);
        Assert.True(autoCodex.Active);
        Assert.False(autoCodex.ShowNormalStatus);
        Assert.True(autoCodex.ShowRefreshProgress);

        var bothBackground = CombinedRefreshCoordinator.Present(true, true);
        Assert.False(bothBackground.Enabled);
        Assert.True(bothBackground.Active);
        Assert.False(bothBackground.ShowNormalStatus);
        Assert.True(bothBackground.ShowRefreshProgress);

        var manual = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
        Assert.False(manual.Enabled);
        Assert.True(manual.Active);
        Assert.Equal(UiText.RefreshAllProgress, manual.ProgressText);
        Assert.False(manual.ShowNormalStatus);
        Assert.True(manual.ShowRefreshProgress);

        var restored = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(restored.Enabled);
        Assert.False(restored.Active);
        Assert.True(restored.ShowNormalStatus);
        Assert.False(restored.ShowRefreshProgress);
    }

    [Fact]
    public void FlyoutHeader_ShowsExactlyOneActiveRefreshLabel()
    {
        var idle = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(idle.ShowNormalStatus);
        Assert.False(idle.ShowRefreshProgress);

        var chatgpt = CombinedRefreshCoordinator.Present(true, false);
        Assert.False(chatgpt.ShowNormalStatus);
        Assert.True(chatgpt.ShowRefreshProgress);

        var combined = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
        Assert.False(combined.ShowNormalStatus);
        Assert.True(combined.ShowRefreshProgress);

        var done = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(done.ShowNormalStatus);
        Assert.False(done.ShowRefreshProgress);

        var flyoutCode = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("StatusText.Visibility = presentation.ShowNormalStatus", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("RefreshProgressText.Visibility = presentation.ShowRefreshProgress", flyoutCode, StringComparison.Ordinal);
        Assert.Contains("ApplyRefreshIndicator(presentation.Active)", flyoutCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Margin=\"0,0,-", File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombinedRefresh_KeepsIndependentResults_AndAlwaysReleasesBusy()
    {
        var coordinator = new CombinedRefreshCoordinator(
            (_, _) => Task.FromResult(new SyncOutcome(AppSyncStatus.UpToDate, null, 3)),
            _ => Task.FromResult(new CodexRefreshResult(
                CodexQuotaSnapshot.Empty(CodexQuotaStatus.CodexNotFound, "missing"),
                false,
                "codex-not-found")));
        CombinedRefreshResult? observed = null;
        try
        {
            observed = await coordinator.RefreshAllAsync(true, CancellationToken.None);
            Assert.Equal(AppSyncStatus.UpToDate, observed.ChatGpt?.Status);
            Assert.Equal(CodexQuotaStatus.CodexNotFound, observed.Codex.Snapshot.Status);
            Assert.True(observed.PartialFailure);
            Assert.False(observed.TotalFailure);
        }
        finally
        {
            Assert.False(coordinator.ChatGptRefreshing);
            Assert.False(coordinator.CodexRefreshing);
            Assert.True(coordinator.RefreshButtonEnabled);
        }

        var failing = new CombinedRefreshCoordinator(
            (_, _) => throw new InvalidOperationException("boom"),
            _ => Task.FromResult(new CodexRefreshResult(
                new CodexQuotaSnapshot(CodexQuotaStatus.Available, null, DateTimeOffset.Now, DateTimeOffset.Now, null, null, null, [new CodexQuotaWindow(null, 10, 300, null, CodexWindowKind.FiveHour)], null),
                false,
                null)));
        var afterException = await failing.RefreshAllAsync(true, CancellationToken.None);
        Assert.Equal(AppSyncStatus.Error, afterException.ChatGpt?.Status);
        Assert.Equal(CodexQuotaStatus.Available, afterException.Codex.Snapshot.Status);
        Assert.False(failing.BothRefreshing);
        Assert.True(failing.RefreshButtonEnabled);
    }

    [Fact]
    public void LocalizationKeys_ExistForCodexAndTaskbar()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("Always show taskbar status", UiText.TaskbarStatusEnabled);
            Assert.Equal("Refresh all", UiText.RefreshAll);
            Assert.Equal("Refreshing...", UiText.RefreshAllProgress);
            Assert.Equal("Syncing", UiText.Syncing);
            Assert.Equal("Codex usage", UiText.CodexUsage);
            Assert.Equal("5-hour used", UiText.FiveHourUsed);
            Assert.Equal("5-hour remaining", UiText.FiveHourRemaining);
            Assert.Equal("Weekly used", UiText.WeeklyUsed);
            Assert.Equal("Weekly remaining", UiText.WeeklyRemaining);
            Assert.Equal("Last checked", UiText.LastChecked);
            Assert.Equal("Reset credits", UiText.ResetCredits);
            Assert.Equal("Codex not found", UiText.CodexNotFound);
            Assert.Equal("Sign in to Codex", UiText.CodexSignIn);
            Assert.Equal("Last data shown · stale", UiText.CodexDataStale);
            Assert.Equal("Recent refresh error", UiText.CodexRecentRefreshError);
            Assert.Equal("Protocol changed", UiText.CodexProtocolChanged);
            Assert.Equal("Request timed out", UiText.CodexTimedOut);
            Assert.Equal("Refreshing...", UiText.CodexRefreshing);
            Assert.Equal("Codex executable path (optional)", UiText.CodexExePath);
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("작업표시줄 상시 표시", UiText.TaskbarStatusEnabled);
            Assert.Equal("모두 새로고침", UiText.RefreshAll);
            Assert.Equal("동기화 중...", UiText.RefreshAllProgress);
            Assert.Equal("동기화 중", UiText.Syncing);
            Assert.Equal("5시간 사용량", UiText.FiveHourUsed);
            Assert.Equal("주간 남음", UiText.WeeklyRemaining);
            Assert.Equal("리셋권", UiText.ResetCredits);
            Assert.Equal("Codex를 찾을 수 없음", UiText.CodexNotFound);
            Assert.Equal("Codex에 로그인하세요", UiText.CodexSignIn);
            Assert.Equal("마지막 데이터 표시 · 오래됨", UiText.CodexDataStale);
            Assert.Equal("최근 새로고침 오류", UiText.CodexRecentRefreshError);
            Assert.Equal("프로토콜이 변경됨", UiText.CodexProtocolChanged);
            Assert.Equal("요청 시간이 초과됨", UiText.CodexTimedOut);
            Assert.Equal("새로고침 중...", UiText.CodexRefreshing);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void DurationLabels_DoNotInventFiveHourOrWeekly()
    {
        UiText.SetLanguage(UiLanguage.English);
        Assert.Equal("24-hour", CodexDisplayFormatting.DurationLabel(1440));
        Assert.Equal("3-day", CodexDisplayFormatting.DurationLabel(4320));
        Assert.Equal("17 min", CodexDisplayFormatting.DurationLabel(17));
        Assert.NotEqual("5-hour", CodexDisplayFormatting.DurationLabel(240));
        Assert.NotEqual("Weekly", CodexDisplayFormatting.DurationLabel(240));
    }

    [Fact]
    public void StripWindow_UsesSafeOverlayFlags()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TaskbarStatusStripWindow.xaml"));
        Assert.Contains("WindowStyle=\"None\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowInTaskbar=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowActivated=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("CardBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("TextBrush", xaml, StringComparison.Ordinal);
        var source = File.ReadAllText(Find("src/ProMeter/UI/TaskbarStatusStripWindow.xaml.cs"));
        Assert.Contains("FlyoutRequested", source, StringComparison.Ordinal);
        Assert.Contains("RefreshRequested", source, StringComparison.Ordinal);
        Assert.Contains("ContextMenuRequested", source, StringComparison.Ordinal);
        var win32 = File.ReadAllText(Find("src/ProMeter/UI/TaskbarWin32.cs"));
        Assert.DoesNotContain("SetParent", win32, StringComparison.Ordinal);
        Assert.DoesNotContain("SetWindowsHook", win32, StringComparison.Ordinal);
        Assert.Contains("WsExNoActivate", win32, StringComparison.Ordinal);
        Assert.Contains("~WsExTransparent", win32, StringComparison.Ordinal);
        Assert.Contains("UiCallbackMarshal.TryPost", source, StringComparison.Ordinal);
        Assert.Contains("Dispatcher.BeginInvoke", source, StringComparison.Ordinal);
        Assert.Contains("HasShutdownStarted", source, StringComparison.Ordinal);
        Assert.Contains("HasShutdownFinished", source, StringComparison.Ordinal);
        var systemHandler = source[source.IndexOf("private void OnSystemLayout", StringComparison.Ordinal)..];
        Assert.DoesNotContain("RequestReposition();", systemHandler.Split("Dispatcher.BeginInvoke")[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CombinedManualRefresh_DisablesButtonUntilFinished()
    {
        var gate = new TaskCompletionSource();
        var coordinator = new CombinedRefreshCoordinator(
            async (_, _) =>
            {
                await gate.Task;
                return new SyncOutcome(AppSyncStatus.UpToDate, null, 1);
            },
            async _ =>
            {
                await gate.Task;
                return new CodexRefreshResult(
                    CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available),
                    false,
                    null);
            });

        var running = coordinator.RefreshAllAsync(true, CancellationToken.None);
        Assert.True(coordinator.ManualRefreshInProgress);
        Assert.False(coordinator.RefreshButtonEnabled);
        var busy = CombinedRefreshCoordinator.Present(
            coordinator.ChatGptRefreshing,
            coordinator.CodexRefreshing,
            coordinator.ManualRefreshInProgress);
        Assert.False(busy.Enabled);
        Assert.True(busy.Active);
        Assert.Equal(UiText.RefreshAllProgress, busy.ProgressText);
        gate.SetResult();
        await running;
        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
        var restored = CombinedRefreshCoordinator.Present(false, false, coordinator.ManualRefreshInProgress);
        Assert.True(restored.Enabled);
        Assert.False(restored.Active);
    }

    [Fact]
    public async Task CombinedManualRefresh_CancellationReenablesButton()
    {
        using var cts = new CancellationTokenSource();
        var coordinator = new CombinedRefreshCoordinator(
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new SyncOutcome(AppSyncStatus.UpToDate, null, 0);
            },
            async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return new CodexRefreshResult(
                    CodexQuotaSnapshot.Empty(CodexQuotaStatus.Cancelled),
                    false,
                    "cancelled");
            });
        var running = coordinator.RefreshAllAsync(true, cts.Token);
        Assert.False(coordinator.RefreshButtonEnabled);
        cts.Cancel();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(coordinator.ManualRefreshInProgress);
        Assert.True(coordinator.RefreshButtonEnabled);
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
