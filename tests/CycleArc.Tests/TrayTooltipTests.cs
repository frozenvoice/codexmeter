using CycleArc.Services;

namespace CycleArc.Tests;

public class TrayTooltipTests
{
    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void CompactTrayTooltip_FitsNotifyIconLimit(UiLanguage language)
    {
        WithLanguage(language, () =>
        {
            var text = DisplayFormatting.TrayTooltip(LiveStartupSnapshot());

            Assert.True(text.Length < 128);
            Assert.True(text.Length <= NotifyIconText.MaximumLength);
            Assert.Contains(UiText.ExactRemainingUnavailable, text, StringComparison.Ordinal);
            Assert.DoesNotContain("29+", text, StringComparison.Ordinal);
            Assert.DoesNotContain("29 / 50", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Remaining: 21", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EveryLocalizedStatusAndWorstCaseCount_FitsNotifyIconLimit()
    {
        foreach (var language in Enum.GetValues<UiLanguage>())
        {
            WithLanguage(language, () =>
            {
                foreach (var status in Enum.GetValues<AppSyncStatus>())
                {
                    foreach (var unavailable in new[] { false, true })
                    {
                        var text = DisplayFormatting.TrayTooltip(new QuotaSnapshot
                        {
                            Used = int.MaxValue,
                            Limit = int.MaxValue,
                            DisplayUsageUnavailable = unavailable,
                            Status = status
                        });

                        Assert.True(text.Length < 128, $"{language}/{status}/{unavailable}: {text.Length}");
                    }
                }
            });
        }
    }

    [Fact]
    public void UnavailableUsage_UsesQuestionMarksInCompactTooltip()
    {
        WithLanguage(UiLanguage.English, () =>
        {
            var text = DisplayFormatting.TrayTooltip(new QuotaSnapshot
            {
                Limit = 50,
                DisplayUsageUnavailable = true,
                Status = AppSyncStatus.PartialData
            });

            Assert.Contains(UiText.ExactRemainingUnavailable, text, StringComparison.Ordinal);
            Assert.DoesNotContain("? / 50", text, StringComparison.Ordinal);
            Assert.DoesNotContain("0 / 50", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Remaining: ?", text, StringComparison.Ordinal);
            Assert.True(text.Length < 128);
        });
    }

    [Fact]
    public void SyncingSnapshot_UsesSyncingCompactTooltip()
    {
        WithLanguage(UiLanguage.Korean, () =>
        {
            var text = DisplayFormatting.TrayTooltip(new QuotaSnapshot
            {
                Used = 29,
                Limit = 50,
                IsSyncing = true,
                LastSync = DateTimeOffset.UtcNow,
                Status = AppSyncStatus.Syncing
            });

            Assert.Contains(UiText.SyncingEllipsis, text, StringComparison.Ordinal);
            Assert.True(text.Length < 128);
        });
    }

    [Fact]
    public void SafeText_BoundsMultilineInputWithoutNewlineExpansion()
    {
        var multiline = string.Join("\r\n", Enumerable.Repeat(new string('x', 80), 20));

        var safe = NotifyIconText.Safe(multiline);

        Assert.True(safe.Length < 128);
        Assert.DoesNotContain("\r", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeText_TruncationDoesNotEndOnHighSurrogate()
    {
        var text = new string('x', NotifyIconText.MaximumLength - 1) + "\U0001F600";

        var safe = NotifyIconText.Safe(text);

        Assert.True(safe.Length < 128);
        Assert.False(char.IsHighSurrogate(safe[^1]));
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void LiveStartupSnapshot_TrayBoundaryDoesNotThrow(UiLanguage language)
    {
        WithLanguage(language, () =>
        {
            var snapshot = LiveStartupSnapshot();
            string? nativeText = null;

            var exception = Record.Exception(() =>
                nativeText = NotifyIconText.Safe(DisplayFormatting.TrayTooltip(snapshot)));

            Assert.Null(exception);
            Assert.NotNull(nativeText);
            Assert.True(nativeText.Length < 128);
            Assert.False(char.IsHighSurrogate(nativeText[^1]));

            var detailed = DisplayFormatting.Tooltip(snapshot);
            Assert.Contains(UiText.ServerReset, detailed, StringComparison.Ordinal);
            Assert.Contains(UiText.ExactRemaining, detailed, StringComparison.Ordinal);
            Assert.Contains(UiText.LastSync, detailed, StringComparison.Ordinal);
            Assert.Contains(UiText.DataStatus, detailed, StringComparison.Ordinal);
            Assert.True(detailed.Length > nativeText.Length);
            Assert.DoesNotContain("29 / 50", detailed, StringComparison.Ordinal);
        });
    }

    private static QuotaSnapshot LiveStartupSnapshot()
    {
        var resetLocal = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Unspecified);
        var syncLocal = new DateTime(2026, 9, 5, 2, 45, 37, DateTimeKind.Unspecified);
        return new QuotaSnapshot
        {
            Used = 29,
            Limit = 50,
            ReconstructedUsed = 29,
            ResetAt = new DateTimeOffset(resetLocal, TimeZoneInfo.Local.GetUtcOffset(resetLocal)),
            ResetAnchorSource = ResetAnchorSource.Default,
            ResetEstimated = true,
            LastSync = new DateTimeOffset(syncLocal, TimeZoneInfo.Local.GetUtcOffset(syncLocal)),
            Status = AppSyncStatus.PartialData,
            Coverage = new CoverageInfo()
        };
    }

    private static void WithLanguage(UiLanguage language, Action assertion)
    {
        var previous = UiText.Language;
        UiText.SetLanguage(language);
        try
        {
            assertion();
        }
        finally
        {
            UiText.SetLanguage(previous);
        }
    }
}
