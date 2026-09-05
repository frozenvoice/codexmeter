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
    }

    [Fact]
    public void RefreshPresentation_DisablesOnlyWhenBothBusy_AndRestores()
    {
        Assert.True(CombinedRefreshCoordinator.Present(false, false).Enabled);
        Assert.True(CombinedRefreshCoordinator.Present(true, false).Enabled);
        Assert.True(CombinedRefreshCoordinator.Present(false, true).Enabled);
        var both = CombinedRefreshCoordinator.Present(true, true);
        Assert.False(both.Enabled);
        Assert.True(both.Active);
        var restored = CombinedRefreshCoordinator.Present(false, false);
        Assert.True(restored.Enabled);
        Assert.False(restored.Active);
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
            Assert.Equal("Protocol changed", UiText.CodexProtocolChanged);
            Assert.Equal("Request timed out", UiText.CodexTimedOut);
            Assert.Equal("Refreshing...", UiText.CodexRefreshing);
            Assert.Equal("Codex executable path (optional)", UiText.CodexExePath);
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("작업표시줄 상시 표시", UiText.TaskbarStatusEnabled);
            Assert.Equal("모두 새로고침", UiText.RefreshAll);
            Assert.Equal("5시간 사용량", UiText.FiveHourUsed);
            Assert.Equal("주간 남음", UiText.WeeklyRemaining);
            Assert.Equal("리셋권", UiText.ResetCredits);
            Assert.Equal("Codex를 찾을 수 없음", UiText.CodexNotFound);
            Assert.Equal("Codex에 로그인하세요", UiText.CodexSignIn);
            Assert.Equal("마지막 데이터 표시 · 오래됨", UiText.CodexDataStale);
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
