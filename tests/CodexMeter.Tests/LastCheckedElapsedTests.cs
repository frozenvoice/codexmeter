using CodexMeter.Codex;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class LastCheckedElapsedTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T21:08:00+09:00");

    [Theory]
    [InlineData(0, "Just now", "방금 전")]
    [InlineData(59, "Just now", "방금 전")]
    [InlineData(60, "1m ago", "1분 전")]
    [InlineData(179, "2m ago", "2분 전")]
    [InlineData(180, "3m ago", "3분 전")]
    [InlineData(3599, "59m ago", "59분 전")]
    [InlineData(3600, "1h ago", "1시간 전")]
    [InlineData(86399, "23h ago", "23시간 전")]
    [InlineData(86400, "1d ago", "1일 전")]
    public void ElapsedUsesCompletedUnitsInBothLanguages(int seconds, string english, string korean)
    {
        var previous = UiText.Language;
        try
        {
            UiText.SetLanguage(UiLanguage.English);
            Assert.Equal(english, CodexDeadlineFormatting.Elapsed(Now.AddSeconds(-seconds), Now));
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal(korean, CodexDeadlineFormatting.Elapsed(Now.AddSeconds(-seconds), Now));
        }
        finally { UiText.SetLanguage(previous); }
    }

    [Fact]
    public void MissingOrFutureTimestampDoesNotInventElapsedTime()
    {
        Assert.Null(CodexDeadlineFormatting.Elapsed(null, Now));
        Assert.Null(CodexDeadlineFormatting.Elapsed(Now.AddSeconds(1), Now));
    }

    [Fact]
    public void RowAgesFromLastSuccessRatherThanFailedAttempt()
    {
        var previous = UiText.Language;
        try
        {
            UiText.SetLanguage(UiLanguage.Korean);
            var checkedAt = Now.AddMinutes(-3);
            var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Stale, "pro", checkedAt, Now,
                null, null, null, [new CodexQuotaWindow("codex", 28, 10080, Now.AddDays(7), CodexWindowKind.Weekly)], null);
            var row = Assert.Single(CodexDisplayFormatting.Rows(snapshot, Now), x => x.Label == UiText.LastChecked);
            Assert.Equal($"{checkedAt.ToLocalTime():HH:mm} · 3분 전", row.Value);
            var later = Assert.Single(CodexDisplayFormatting.Rows(snapshot, Now.AddMinutes(1)), x => x.Label == UiText.LastChecked);
            Assert.EndsWith(" · 4분 전", later.Value);
            Assert.DoesNotContain(CodexDisplayFormatting.Rows(snapshot with { LastSuccessfulRefresh = null }, Now),
                x => x.Label == UiText.LastChecked);
        }
        finally { UiText.SetLanguage(previous); }
    }
}
