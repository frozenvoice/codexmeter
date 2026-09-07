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
    public void ConfirmedCycle_ReconstructedCount_RemainsEstimatedNeverAuthoritative()
    {
        // CurrentCycleKnown=true only means the period boundary is trusted for
        // counting - it must never be presented as a server-authoritative count.
        var snapshot = Usable(reconstructed: 12, currentCycleKnown: true);
        snapshot.Limit = 50;
        var presentation = ProStatusPresentation.From(snapshot);

        Assert.False(presentation.ExactRemainingAvailable);
        Assert.Contains("estimated", presentation.ConfirmedRequestsText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
        Assert.DoesNotContain("50", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDisplayState_CoversAllFourStatesDistinctly()
    {
        var authoritative = new QuotaSnapshot { UsesServerCount = true, ReconstructedUsed = 4, Used = 4, Limit = 50 };
        Assert.Equal(ReconstructionDisplayState.AuthoritativeServerCount, ProStatusPresentation.ResolveDisplayState(authoritative));

        var unavailable = new QuotaSnapshot { DisplayUsageUnavailable = true };
        Assert.Equal(ReconstructionDisplayState.Unavailable, ProStatusPresentation.ResolveDisplayState(unavailable));

        var knownCycle = Usable(reconstructed: 12, currentCycleKnown: true);
        Assert.Equal(ReconstructionDisplayState.EstimatedKnownCycle, ProStatusPresentation.ResolveDisplayState(knownCycle));

        var fallbackPeriod = Usable(reconstructed: 12, currentCycleKnown: false);
        Assert.Equal(ReconstructionDisplayState.EstimatedFallbackPeriod, ProStatusPresentation.ResolveDisplayState(fallbackPeriod));
    }

    [Fact]
    public void ReconstructedLabel_OnlyDiffersForEstimatedFallbackPeriod()
    {
        var knownCycle = Usable(reconstructed: 12, currentCycleKnown: true);
        Assert.Equal(UiText.CurrentCycleReconstructed, ProStatusPresentation.From(knownCycle).ReconstructedLabel);

        var fallbackPeriod = Usable(reconstructed: 12, currentCycleKnown: false);
        Assert.Equal(UiText.EstimatedPeriodReconstructed, ProStatusPresentation.From(fallbackPeriod).ReconstructedLabel);
        Assert.NotEqual(UiText.CurrentCycleReconstructed, ProStatusPresentation.From(fallbackPeriod).ReconstructedLabel);
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
            Assert.Equal("예상 다음 리셋", koreanReset.EstimateLabel);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void UnconfirmedCycle_NeverManufacturesRemainingFromConfiguredQuota()
    {
        var snapshot = Usable(reconstructed: 38, currentCycleKnown: false);
        snapshot.Limit = 50;
        var presentation = ProStatusPresentation.From(snapshot);

        // Remaining must never be computed as Limit - ReconstructedUsed; only a
        // server-authoritative snapshot may show a remaining count at all.
        Assert.False(presentation.ExactRemainingAvailable);
        Assert.Equal(UiText.ExactRemainingUnavailable, presentation.ExactRemainingText);
        Assert.DoesNotContain((snapshot.Limit - snapshot.ReconstructedUsed).ToString(CultureInfo.InvariantCulture), presentation.ExactRemainingText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Synthetic reproduction of the real-world log: a manual sync completed
    /// (status=UpToDate, coverage=Estimated, loaded=6 failed=0) with GPT Pro
    /// observations present, but no server-confirmed cycle/reset boundary. The
    /// Flyout must show the reconstructed estimate, not "Unavailable".
    /// </summary>
    [Fact]
    public void RealLogScenario_SuccessfulSyncWithUnconfirmedCycle_ShowsEstimateNotUnavailable()
    {
        var snapshot = new QuotaSnapshot
        {
            Status = AppSyncStatus.UpToDate,
            CurrentCycleKnown = false,
            DisplayUsageUnavailable = false,
            ReconstructedUsed = 38,
            ResetAnchorSource = ResetAnchorSource.Default,
            ResetAt = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero),
            ResetEstimated = true,
            LastSync = DateTimeOffset.UtcNow,
            Coverage = new CoverageInfo
            {
                NormalChats = true,
                CountConfidence = CoverageConfidence.Estimated
            }
        };

        var presentation = ProStatusPresentation.From(snapshot);
        var reset = DisplayFormatting.ResetDisplay(snapshot);

        Assert.Equal(UiText.ReconstructedCount(38), presentation.ConfirmedRequestsText);
        Assert.NotEqual(UiText.Unavailable, presentation.ConfirmedRequestsText);
        Assert.Equal(UiText.EstimatedPeriodReconstructed, presentation.ReconstructedLabel);
        Assert.Equal(ReconstructionDisplayState.EstimatedFallbackPeriod, presentation.DisplayState);

        Assert.Equal("", reset.TimeValue);
        Assert.Equal(UiText.EstimatedNextReset, reset.EstimateLabel);
        Assert.NotNull(reset.EstimateValue);

        Assert.False(presentation.ExactRemainingAvailable);
        Assert.Equal(UiText.ExactRemainingUnavailable, presentation.ExactRemainingText);
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

    [Fact]
    public void FlyoutAndMainWindow_BothBindTheSharedReconstructedLabel()
    {
        // AGENTS.md: every visible UI surface must use the same Pro status
        // presentation semantics - the label must come from ProStatusPresentation,
        // not be independently hardcoded per window.
        var flyout = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("ConfirmedUsageLabel.Text = presentation.ReconstructedLabel", flyout, StringComparison.Ordinal);

        var main = File.ReadAllText(Find("src/ProMeter/UI/MainWindow.xaml.cs"));
        Assert.Contains("presentation.ReconstructedLabel", main, StringComparison.Ordinal);
        Assert.DoesNotContain("UiText.CurrentCycleReconstructed", main, StringComparison.Ordinal);
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
