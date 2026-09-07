using ProMeter.Services;

namespace ProMeter.Tests;

/// <summary>
/// Regression coverage for the "GPT PRO shows Unavailable after a successful sync"
/// bug: a confirmed reconstruction must never be hidden behind "Unavailable" merely
/// because the server reset/cycle boundary itself is unconfirmed. It must show an
/// explicit ESTIMATED count for the estimated period - never an authoritative count,
/// never a fabricated lower bound.
/// </summary>
public class GptProEstimatedPeriodPresentationTests
{
    [Fact]
    public void ConfirmedCycle_UsableReconstruction_ShowsCount()
    {
        var snapshot = Usable(reconstructed: 12, currentCycleKnown: true);
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.Equal(UiText.ReconstructedCount(12), presentation.ConfirmedRequestsText);
        Assert.NotEqual(UiText.Unavailable, presentation.ConfirmedRequestsText);
    }

    [Fact]
    public void UnconfirmedCycle_EstimatedFallbackPeriod_UsableReconstruction_ShowsEstimatedCount_NotUnavailable()
    {
        var snapshot = Usable(reconstructed: 38, currentCycleKnown: false);
        Assert.Equal(AppSyncStatus.UpToDate, snapshot.Status);
        var presentation = ProStatusPresentation.From(snapshot);

        Assert.Equal(UiText.ReconstructedCount(38), presentation.ConfirmedRequestsText);
        Assert.Equal(UiText.ReconstructedCount(38), presentation.ReconstructedText);
        Assert.NotEqual(UiText.Unavailable, presentation.ConfirmedRequestsText);
        Assert.DoesNotContain(UiText.Unavailable, presentation.ConfirmedRequestsText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfirmedCycle_NeverShowsServerOrAuthoritativeWording()
    {
        var snapshot = Usable(reconstructed: 38, currentCycleKnown: false);
        var presentation = ProStatusPresentation.From(snapshot);

        Assert.False(presentation.ExactRemainingAvailable);
        Assert.False(presentation.HasServerReset);
        Assert.DoesNotContain(UiText.ServerReset, presentation.ConfirmedRequestsText, StringComparison.Ordinal);
        Assert.DoesNotContain("38 / ", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfirmedCycle_ShowsSingleEstimatedResetRow_NotAConfirmedTimeRow()
    {
        var snapshot = Usable(reconstructed: 38, currentCycleKnown: false);
        var display = DisplayFormatting.ResetDisplay(snapshot);

        // Exactly one row's worth of information: the estimate. No separate
        // "Reset time: Not confirmed" row alongside it.
        Assert.Equal("", display.TimeLabel);
        Assert.Equal("", display.TimeValue);
        Assert.Equal(UiText.EstimatedNextReset, display.EstimateLabel);
        Assert.NotNull(display.EstimateValue);
        Assert.DoesNotContain(UiText.NotConfirmed, display.TimeValue, StringComparison.Ordinal);
    }

    [Fact]
    public void GenuinelyUnusableReconstruction_StaysUnavailable()
    {
        var snapshot = new QuotaSnapshot
        {
            DisplayUsageUnavailable = true,
            ReconstructedUsed = 0,
            CurrentCycleKnown = false,
            Coverage = new CoverageInfo { CountConfidence = CoverageConfidence.Incomplete }
        };

        var text = ProStatusPresentation.FormatReconstructedCount(snapshot);
        Assert.NotEqual(UiText.ReconstructedCount(0), text);
        Assert.Equal("", ProStatusPresentation.CompactReconstructedToken(snapshot));
    }

    [Fact]
    public void UnconfirmedCycle_CompactTaskbarToken_IsEstimatedNotLowerBound()
    {
        var snapshot = Usable(reconstructed: 38, currentCycleKnown: false);
        var token = ProStatusPresentation.CompactReconstructedToken(snapshot);
        Assert.Equal("38~", token);
        Assert.DoesNotContain("+", token, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfirmedCycle_DoesNotCombineWithAConfiguredDenominator()
    {
        var snapshot = Usable(reconstructed: 38, currentCycleKnown: false);
        snapshot.Limit = 50;
        var presentation = ProStatusPresentation.From(snapshot);

        Assert.DoesNotContain("/", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
        Assert.DoesNotContain("50", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
    }

    [Fact]
    public void UnconfirmedCycle_KoreanAndEnglishWording()
    {
        var snapshot = Usable(reconstructed: 38, currentCycleKnown: false);

        UiText.SetLanguage(UiLanguage.English);
        var english = ProStatusPresentation.From(snapshot);
        var englishReset = DisplayFormatting.ResetDisplay(snapshot);
        Assert.Equal("38 · estimated", english.ConfirmedRequestsText);
        Assert.Equal("Estimated next reset", englishReset.EstimateLabel);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = ProStatusPresentation.From(snapshot);
            var koreanReset = DisplayFormatting.ResetDisplay(snapshot);
            Assert.Equal("38회 · 추정", korean.ConfirmedRequestsText);
            Assert.Equal("추정 다음 리셋", koreanReset.EstimateLabel);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void FlyoutCodeBehind_HidesResetTimeRowWhenThereIsNothingConfirmedToShow()
    {
        var source = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        var hasRowIndex = source.IndexOf("hasResetTimeRow", StringComparison.Ordinal);
        Assert.True(hasRowIndex >= 0, "FlyoutWindow.xaml.cs must gate the Reset time row on whether there is a confirmed/labeled value.");
        Assert.Contains("ResetTimeLabel.Visibility = hasResetTimeRow", source, StringComparison.Ordinal);
        Assert.Contains("ResetText.Visibility = hasResetTimeRow", source, StringComparison.Ordinal);
    }

    private static string Find(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProMeter.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
        return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static QuotaSnapshot Usable(int reconstructed, bool currentCycleKnown) => new()
    {
        ReconstructedUsed = reconstructed,
        CurrentCycleKnown = currentCycleKnown,
        DisplayUsageUnavailable = false,
        Status = AppSyncStatus.UpToDate,
        ResetAt = DateTimeOffset.UtcNow.AddDays(7),
        ResetAnchorSource = ResetAnchorSource.Default,
        Coverage = new CoverageInfo { NormalChats = true, CountConfidence = CoverageConfidence.Estimated }
    };
}
