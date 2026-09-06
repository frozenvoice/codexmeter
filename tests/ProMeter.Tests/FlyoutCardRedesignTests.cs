using ProMeter.Codex;
using ProMeter.Models;
using ProMeter.Services;

namespace ProMeter.Tests;

public class FlyoutCardRedesignTests
{
    [Fact]
    public void NewBadgeAndNoticeStrings_ExistInKoreanAndEnglish()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("History-based estimate", UiText.HistoryBasedEstimateBadge);
            Assert.Equal("Server-based · accurate", UiText.ServerBasedAccurateBadge);
            Assert.Equal("Some history is being revalidated.", UiText.PartialRevalidationNotice);
            Assert.Equal("Used", UiText.CodexLegendUsed);
            Assert.Equal("Remaining", UiText.CodexLegendRemaining);
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("기록 기반 추정", UiText.HistoryBasedEstimateBadge);
            Assert.Equal("서버 기반 정확", UiText.ServerBasedAccurateBadge);
            Assert.Equal("일부 기록 재검증 중", UiText.PartialRevalidationNotice);
            Assert.Equal("사용", UiText.CodexLegendUsed);
            Assert.Equal("남음", UiText.CodexLegendRemaining);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void CountWithUnit_AddsKoreanCounterSuffixOnlyInKorean()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("5", DisplayFormatting.CountWithUnit(5));
            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("5회", DisplayFormatting.CountWithUnit(5));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void HasReliabilityConcern_TrueOnlyWhenUnresolvedOrLegacyEvidenceExists()
    {
        var clean = new QuotaSnapshot { UnresolvedCount = 0, LegacyPendingCount = 0 };
        Assert.False(ProStatusPresentation.HasReliabilityConcern(clean));

        var unresolved = new QuotaSnapshot { UnresolvedCount = 3 };
        Assert.True(ProStatusPresentation.HasReliabilityConcern(unresolved));

        var legacy = new QuotaSnapshot { LegacyPendingCount = 2 };
        Assert.True(ProStatusPresentation.HasReliabilityConcern(legacy));
    }

    [Theory]
    [InlineData(UserFacingHealthKind.Usable, StatusToneKind.Ok)]
    [InlineData(UserFacingHealthKind.Syncing, StatusToneKind.Accent)]
    [InlineData(UserFacingHealthKind.NeedsConnection, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.NeedsSignIn, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.SyncFailed, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.NeedsAttention, StatusToneKind.Danger)]
    [InlineData(UserFacingHealthKind.Stale, StatusToneKind.Muted)]
    public void HealthTone_MapsExpectedSeverity(UserFacingHealthKind kind, StatusToneKind expected)
    {
        Assert.Equal(expected, UserFacingHealthTone.From(kind));
    }

    [Fact]
    public void FlyoutXaml_UsesCardLayoutWithRequiredElements()
    {
        var xaml = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml"));

        // Four cards.
        Assert.Contains("x:Name=\"GptProCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CodexCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReasoningCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StatusCard\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource FlyoutCard}\"", xaml, StringComparison.Ordinal);

        // GPT Pro: unavailable/unhelpful rows must not be unconditionally present.
        Assert.Contains("x:Name=\"GptProRestrictionBanner\" Visibility=\"Collapsed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ExactRemainingPanel\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReliabilityNoticeText\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("923", xaml, StringComparison.Ordinal);

        // Codex ring primitives (no external chart library — Path/Ellipse only).
        Assert.Contains("x:Name=\"CodexRingTrack\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CodexRingArcPath\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ArcSegment", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CodexRingFullCircle\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CodexRingValueText\"", xaml, StringComparison.Ordinal);

        // Sol Reasoning: plain circle (no progress arc) + count-comparison bars.
        Assert.Contains("x:Name=\"ReasonWeekCenterValue\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReasonTodayBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReasonExtraBar\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ExtraHighAccentBrush", xaml, StringComparison.Ordinal);

        // No external UI/chart library — only WPF primitives.
        Assert.DoesNotContain("http://schemas.microsoft.com/winfx/2006/xaml/presentation/oxyplot", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("livecharts", xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlyoutCodeBehind_HidesUnavailableRowsAndUsesPureHelpers()
    {
        var code = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("ExactRemainingPanel.Visibility = presentation.ExactRemainingAvailable", code, StringComparison.Ordinal);
        Assert.Contains("GptProRestrictionBanner.Visibility = presentation.Restricted", code, StringComparison.Ordinal);
        Assert.Contains("ProStatusPresentation.HasReliabilityConcern(snapshot)", code, StringComparison.Ordinal);
        Assert.Contains("CodexRingPresentation.From(snapshot)", code, StringComparison.Ordinal);
        Assert.Contains("RingGeometry.ComputeUsedArc(", code, StringComparison.Ordinal);
        Assert.Contains("SolBarSet.Compute(reasoning)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("UnresolvedPendingCount", code, StringComparison.Ordinal);
    }

    [Fact]
    public void Themes_DefineFlyoutCardPaletteAndRuntimeOverridesForBothThemes()
    {
        var themes = File.ReadAllText(Find("src/ProMeter/UI/Themes.xaml"));
        Assert.Contains("x:Key=\"PanelBrush\"", themes, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ExtraHighAccentBrush\"", themes, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"FlyoutCard\"", themes, StringComparison.Ordinal);

        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("app.Resources[\"PanelBrush\"]", app, StringComparison.Ordinal);
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
