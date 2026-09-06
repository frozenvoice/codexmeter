using ProMeter.Codex;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ProStatusPresentationTests
{
    [Fact]
    public void ReconstructedCount_DoesNotFormatAsExactQuota()
    {
        var snapshot = Reconstructed(31, 50);
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.False(presentation.ExactRemainingAvailable);
        Assert.True(presentation.ShowHistoryLowerBound);
        Assert.Equal("31+", presentation.ReconstructedText);
        Assert.Equal(UiText.HistoryBasedLowerBound, presentation.HistoryLowerBoundCaption);
        Assert.Equal(UiText.ExactRemainingUnavailable, presentation.ExactRemainingText);
        Assert.Equal("31+", DisplayFormatting.UsageLabel(snapshot));
        Assert.DoesNotContain("31 / 50", DisplayFormatting.UsageLabel(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("remaining 19", DisplayFormatting.TrayTooltip(snapshot), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Remaining: 19", DisplayFormatting.TrayTooltip(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("31/50", TaskbarStatusFormatter.Format(snapshot, Codex(), TaskbarStripMode.Full), StringComparison.Ordinal);
    }

    [Fact]
    public void RestrictedWithServerReset_FormatsLocalReset()
    {
        var reset = new DateTimeOffset(2026, 9, 6, 5, 20, 13, TimeSpan.Zero);
        var snapshot = Reconstructed(31, 50);
        snapshot.ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.CorrelatedRestriction,
            ResetAt = reset,
            ResetConfidence = ServerResetConfidence.Server,
            ObservedAt = reset.AddHours(-20)
        };
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.True(presentation.Restricted);
        Assert.True(presentation.HasServerReset);
        Assert.Equal(UiText.ProRestricted, presentation.ProStateText.Replace(" · " + UiText.Stale, "", StringComparison.Ordinal));
        Assert.Contains(DisplayFormatting.FormatStamp(reset), presentation.ResetText, StringComparison.Ordinal);
        Assert.Equal("P! 31+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Full));
        Assert.Equal("P!31+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Compact));
        Assert.Equal("P!31+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.UltraCompact));
        var tooltip = TaskbarStatusFormatter.Tooltip(snapshot, Codex());
        Assert.Contains(UiText.ServerReset, tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("31+", tooltip, StringComparison.Ordinal);
        Assert.DoesNotContain("31/50", tooltip, StringComparison.Ordinal);
        Assert.Equal(UiText.HistoryBasedLowerBound, presentation.CountSourceText);
        Assert.True(presentation.ShowHistoryLowerBound);
    }

    [Fact]
    public void NoRestriction_UsesObservedWording()
    {
        var snapshot = Reconstructed(31, 50);
        snapshot.ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.NoCorrelatedRestrictionObserved,
            ResetAt = new DateTimeOffset(2026, 9, 6, 5, 20, 13, TimeSpan.Zero),
            ResetConfidence = ServerResetConfidence.Server
        };
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.Equal(UiText.ProNoServerRestriction, presentation.ProStateText);
        Assert.Equal("P 31+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Full));
        Assert.Equal("P31+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Compact));
        Assert.Equal("P31+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.UltraCompact));
    }

    [Fact]
    public void Unknown_FormatsUnknown()
    {
        var snapshot = Reconstructed(31, 50);
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.Equal(UiText.Unavailable, presentation.ProStateText);
        Assert.Equal("P? 31+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Full));
        Assert.Equal("?", DisplayFormatting.TrayIconText(snapshot));
    }

    [Fact]
    public void ExactXY_OnlyWithAuthoritativeServerCount()
    {
        var snapshot = new QuotaSnapshot
        {
            Used = 7,
            Limit = 50,
            ReconstructedUsed = 4,
            UsesServerCount = true,
            ResetAnchorSource = ResetAnchorSource.Server,
            ResetAt = DateTimeOffset.UtcNow.AddDays(1),
            ProServerStatus = new ProServerStatus
            {
                ServerObserved = true,
                RestrictionState = ProRestrictionState.NoCorrelatedRestrictionObserved,
                ResetConfidence = ServerResetConfidence.Server,
                ResetAt = DateTimeOffset.UtcNow.AddDays(1)
            }
        };
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.True(presentation.ExactRemainingAvailable);
        Assert.False(presentation.ShowHistoryLowerBound);
        Assert.Equal("7 / 50", DisplayFormatting.UsageLabel(snapshot));
        Assert.Equal("4+", presentation.ReconstructedText);
        Assert.Equal("4+", presentation.ConfirmedRequestsText);
        Assert.Equal("43", DisplayFormatting.TrayIconText(snapshot));
    }

    [Fact]
    public void AuthoritativeServerUsed_DoesNotReplaceReconstructedHistory()
    {
        var snapshot = new QuotaSnapshot
        {
            Used = 40,
            Limit = 50,
            ReconstructedUsed = 0,
            UsesServerCount = true,
            ResetAnchorSource = ResetAnchorSource.Server,
            ResetAt = DateTimeOffset.UtcNow.AddDays(1),
            ProServerStatus = new ProServerStatus
            {
                ServerObserved = true,
                RestrictionState = ProRestrictionState.NoCorrelatedRestrictionObserved,
                ResetConfidence = ServerResetConfidence.Server,
                ResetAt = DateTimeOffset.UtcNow.AddDays(1)
            }
        };
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.True(presentation.ExactRemainingAvailable);
        Assert.Equal("40 / 50", DisplayFormatting.UsageLabel(snapshot));
        Assert.Equal("0+", presentation.ReconstructedText);
        Assert.Equal("0+", presentation.ConfirmedRequestsText);
        Assert.Contains("40 / 50", presentation.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("40+", presentation.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("40+", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetLines_ShowStatusResetCodexAndHistory()
    {
        var snapshot = Reconstructed(31, 50);
        snapshot.Reasoning = new ReasoningStats { ExtraHigh = 268 };
        snapshot.ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.CorrelatedRestriction,
            ResetAt = new DateTimeOffset(2026, 9, 6, 5, 20, 0, TimeSpan.Zero),
            ResetConfidence = ServerResetConfidence.Server
        };
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.Contains(UiText.ProRestricted, WidgetStatusFormatter.ProLine(presentation), StringComparison.Ordinal);
        var expectedReset = new DateTimeOffset(2026, 9, 6, 5, 20, 0, TimeSpan.Zero)
            .ToLocalTime()
            .ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        Assert.Equal(expectedReset, WidgetStatusFormatter.ResetLine(presentation));
        Assert.Contains("268", WidgetStatusFormatter.HistoryLine(presentation, snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("31+", WidgetStatusFormatter.HistoryLine(presentation, snapshot), StringComparison.Ordinal);
        Assert.Contains("42%", WidgetStatusFormatter.CodexLine(Codex()), StringComparison.Ordinal);
        Assert.DoesNotContain("31/50", WidgetStatusFormatter.HistoryLine(presentation, snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void AmbiguousResets_UseQuestionToken()
    {
        var snapshot = Reconstructed(8, 50);
        snapshot.ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.NoCorrelatedRestrictionObserved,
            HasAmbiguousResets = true,
            ResetConfidence = ServerResetConfidence.Ambiguous
        };
        Assert.Equal("P? 8+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Full));
        Assert.Equal(UiText.MultipleProResets, ProStatusPresentation.From(snapshot).ResetText);
    }

    private static QuotaSnapshot Reconstructed(int used, int limit) => new()
    {
        Used = used,
        Limit = limit,
        ReconstructedUsed = used,
        UsesServerCount = false,
        Coverage = new CoverageInfo { CountConfidence = CoverageConfidence.Estimated }
    };

    private static CodexQuotaSnapshot Codex() => new(
        CodexQuotaStatus.Available,
        null,
        DateTimeOffset.Now,
        DateTimeOffset.Now,
        null,
        null,
        null,
        [new CodexQuotaWindow(null, 42, 10080, null, CodexWindowKind.Weekly)],
        null);
}
