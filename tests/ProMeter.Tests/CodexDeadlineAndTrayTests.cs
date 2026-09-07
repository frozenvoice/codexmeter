using System.Text.Json.Nodes;
using ProMeter.Codex;
using ProMeter.Services;

namespace ProMeter.Tests;

public class CodexDeadlineAndTrayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T05:00:00Z");
    private static CodexQuotaSnapshot Snapshot(int count = 3, IReadOnlyList<DateTimeOffset?>? dates = null) =>
        new(CodexQuotaStatus.Available, "pro", Now, Now, null, null, count,
            [new CodexQuotaWindow("codex", 10, 10080, Now.AddDays(7), CodexWindowKind.Weekly)], null, dates);

    [Theory]
    [InlineData(0, "Awaiting refresh")]
    [InlineData(-60, "Awaiting refresh")]
    [InlineData(30, "Less than a minute")]
    [InlineData(60, "1m left")]
    [InlineData(3600, "1h left")]
    [InlineData(3660, "1h 1m left")]
    [InlineData(86400, "1d left")]
    [InlineData(90000, "1d 1h left")]
    public void Countdown_UsesActualElapsedDuration(double seconds, string expected)
    {
        UiText.SetLanguage(UiLanguage.English);
        Assert.Equal(expected, CodexDeadlineFormatting.Remaining(Now.AddSeconds(seconds), Now));
    }

    [Fact]
    public void UnknownReset_HasNoInventedCountdown()
    {
        Assert.Null(CodexDeadlineFormatting.Remaining(null, Now));
    }

    [Fact]
    public void KoreanCountdown_AndSeparateResetDetail()
    {
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("6일 21시간 남음", CodexDeadlineFormatting.Remaining(Now.AddHours(165), Now));
            var rows = CodexDisplayFormatting.Rows(Snapshot(), Now);
            var reset = Assert.Single(rows, x => x.Label == UiText.Reset);
            Assert.Equal("7일 남음", reset.Detail);
            Assert.DoesNotContain("남음", reset.Value);
        }
        finally { UiText.SetLanguage(UiLanguage.English); }
    }

    [Fact]
    public void IndividualExpiries_PreserveCountAndShowDistinctDates()
    {
        UiText.SetLanguage(UiLanguage.English);
        var snap = Snapshot(dates: [Now.AddDays(14), Now.AddDays(20), Now.AddDays(21)]);
        var expiry = CodexDeadlineFormatting.CreditExpiry(snap, Now);
        Assert.Contains("Sep 21", expiry.Detail);
        Assert.Contains("Sep 27", expiry.Detail);
        Assert.Contains("Sep 28", expiry.Detail);
        Assert.Equal("3", CodexDisplayFormatting.Rows(snap, Now).Single(x => x.Label == UiText.ResetCredits).Value);
    }

    [Fact]
    public void IncompleteExpiryDetails_DoNotPretendToCoverEveryCredit()
    {
        var expiry = CodexDeadlineFormatting.CreditExpiry(Snapshot(10, [Now.AddDays(1)]), Now);
        Assert.Contains("Some expiries unavailable", expiry.Detail);
        Assert.Contains("Some credit expiry details", expiry.Tooltip);
        Assert.Equal("Expiry not provided", CodexDeadlineFormatting.CreditExpiry(Snapshot(2, [null]), Now).Detail);
        Assert.Null(CodexDeadlineFormatting.CreditExpiry(Snapshot(0), Now).Detail);
    }

    [Fact]
    public void Parser_OnlyProjectsAvailableCodexCreditDatesAndDeduplicatesIds()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
        {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":10080}},
         "rateLimitResetCredits":{"availableCount":9,"credits":[
           {"id":"first","resetType":"codexRateLimits","status":"available","expiresAt":1893456000,"title":"private title"},
           {"id":"first","resetType":"codexRateLimits","status":"available","expiresAt":1893456000},
           {"id":"second","resetType":"codexRateLimits","status":"available","expiresAt":null},
           {"id":"third","resetType":"codexRateLimits","status":"consumed","expiresAt":1893456000},
           {"id":"fourth","resetType":"other","status":"available","expiresAt":1893456000},
           {"id":"bad-date","resetType":"codexRateLimits","status":"available","expiresAt":"invalid"}
         ]}}
        """));
        Assert.Equal(9, parsed.ResetCreditsAvailable);
        Assert.Equal(3, parsed.ResetCreditExpirations!.Count);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), parsed.ResetCreditExpirations[0]);
        Assert.Null(parsed.ResetCreditExpirations[1]);
        Assert.Null(parsed.ResetCreditExpirations[2]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("9999999999999")]
    [InlineData("{}")]
    public void InvalidCreditCounts_RemainUnknown(string count)
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300}},\"rateLimitResetCredits\":{\"availableCount\":" + count + "}}"));
        Assert.Null(parsed.ResetCreditsAvailable);
    }

    [Fact]
    public void RootCreditDetails_AreNotMixedWithLegacyBucketDetails()
    {
        var parsed = CodexRateLimitParser.Parse(null, JsonNode.Parse("""
        {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},
         "rateLimitResetCredits":{"availableCount":1,"credits":[]}},
         "rateLimitResetCredits":{"availableCount":7,"credits":null}}
        """));
        Assert.Equal(7, parsed.ResetCreditsAvailable);
        Assert.Null(parsed.ResetCreditExpirations);
    }

    [Fact]
    public void Cache_PreservesOnlyExpiryDatesAndRemainsBackwardCompatible()
    {
        var directory = Path.Combine(Path.GetTempPath(), "codex-expiry-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "snapshot.json");
            var store = new CodexSnapshotStore(path);
            var snap = Snapshot(dates: [Now.AddDays(2), null]);
            store.Save(snap);
            Assert.Equal(snap.ResetCreditExpirations, store.Load()!.ResetCreditExpirations);
            Assert.Equal(snap.ResetCreditExpirations, store.Load()!.AsStale(Now.AddMinutes(5), "timeout").ResetCreditExpirations);
            var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.DoesNotContain("creditId", json.ToJsonString());
            Assert.DoesNotContain("description", json.ToJsonString());
            json.Remove("resetCreditExpirations");
            File.WriteAllText(path, json.ToJsonString());
            Assert.Null(store.Load()!.ResetCreditExpirations);
            Assert.Equal(3, store.Load()!.ResetCreditsAvailable);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Desktop_UsesWindowsNotificationIconAndCannotCreateTaskbarOverlay()
    {
        var app = Read("src/ProMeter/App.xaml.cs");
        Assert.DoesNotContain("TaskbarStatusStripWindow", app);
        Assert.DoesNotContain("ApplyTaskbarStrip", app);
        Assert.Contains("_settings.TaskbarStatusEnabled = false", app);
        var project = Read("src/ProMeter/ProMeter.csproj");
        Assert.Contains("UI\\TaskbarStatusStripWindow.xaml.cs", project);
        Assert.Contains("UI\\TaskbarWin32.cs", project);
        var tray = Read("src/ProMeter/UI/TrayController.cs");
        Assert.Contains("new NotifyIcon", tray);
        Assert.Contains("LeftClick?.Invoke()", tray);
        Assert.DoesNotContain("SetWindowPos", tray);
    }

    [Fact]
    public void Settings_HasFocusedPagesAndNoUnsafeOverlayOption()
    {
        var xaml = Read("src/ProMeter/UI/SettingsWindow.xaml");
        foreach (var name in new[] { "GeneralTab", "WidgetTab", "ConnectionTab", "CancelButton" }) Assert.Contains(name, xaml);
        Assert.DoesNotContain("TaskbarStatusBox", xaml);
        Assert.Contains("OnCancel", xaml);
        Assert.Contains("WidgetBox", xaml);
        Assert.Contains("SettingsSwitch", xaml);
    }

    private static string Read(string path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProMeter.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, path));
    }
}
