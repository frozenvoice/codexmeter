using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

public class CombinedQuotaRowTests
{
    [Theory]
    [InlineData(UiLanguage.Korean, CodexWindowKind.Weekly, "주간 사용량 / 남음")]
    [InlineData(UiLanguage.English, CodexWindowKind.Weekly, "Weekly used / left")]
    [InlineData(UiLanguage.Korean, CodexWindowKind.FiveHour, "5시간 사용량 / 남음")]
    [InlineData(UiLanguage.English, CodexWindowKind.FiveHour, "5-hour used / left")]
    public void UsageAndRemainingShareOneRow(UiLanguage language, CodexWindowKind kind, string label)
    {
        var previous = UiText.Language;
        try
        {
            UiText.SetLanguage(language);
            var snapshot = Snapshot(29, kind);
            var rows = CodexDisplayFormatting.Rows(snapshot);
            Assert.Equal(2, rows.Count); // Combined quota and reset.
            Assert.Equal(label, rows[0].Label);
            Assert.Equal("29% / 71%", rows[0].Value);
            Assert.False(rows[0].EmphasizeDanger);
        }
        finally { UiText.SetLanguage(previous); }
    }

    [Theory]
    [InlineData(0, "0% / 100%", false)]
    [InlineData(100, "100% / 0%", true)]
    public void BoundaryPercentagesKeepOrderAndWarning(double used, string expected, bool danger)
    {
        var row = CodexDisplayFormatting.Rows(Snapshot(used))[0];
        Assert.Equal(expected, row.Value);
        Assert.Equal(danger, row.EmphasizeDanger);
    }

    [Fact]
    public void UnknownUsageDoesNotInventRemaining()
    {
        var row = CodexDisplayFormatting.Rows(Snapshot(null))[0];
        Assert.DoesNotContain("/", row.Value);
        Assert.DoesNotContain("0%", row.Value);
        Assert.False(row.EmphasizeDanger);
    }

    private static CodexQuotaSnapshot Snapshot(double? used, CodexWindowKind kind = CodexWindowKind.Weekly) =>
        new(CodexQuotaStatus.Available, "pro", null, null, null, null, null,
            [new CodexQuotaWindow("codex", used, kind == CodexWindowKind.Weekly ? 10080 : 300, null, kind)], null);
}
