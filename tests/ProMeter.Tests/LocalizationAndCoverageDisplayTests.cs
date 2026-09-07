using System.Globalization;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class LocalizationAndCoverageDisplayTests
{
    [Fact]
    public void WebViewFallbackStrings_ExistInEnglishAndKorean()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("Checking ChatGPT session...", UiText.CheckingChatGptSession);
            Assert.Equal("WebView2 initialization timed out.", UiText.WebViewInitializationTimedOut);
            Assert.Equal("WebView2 initialization failed.", UiText.WebViewInitializationFailed);
            Assert.Equal("WebView2 navigation timed out.", UiText.WebViewNavigationTimedOut);
            Assert.Equal("WebView2 navigation failed.", UiText.WebViewNavigationFailed);
            Assert.Equal("WebView2 request timed out.", UiText.WebViewRequestTimedOut);
            Assert.Equal("WebView2 request failed.", UiText.WebViewRequestFailed);
            Assert.Equal("WebView2 fallback", UiText.TransportWebView);

            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("ChatGPT 세션을 확인하는 중...", UiText.CheckingChatGptSession);
            Assert.Equal("WebView2 초기화 시간이 초과되었습니다.", UiText.WebViewInitializationTimedOut);
            Assert.Equal("WebView2 초기화에 실패했습니다.", UiText.WebViewInitializationFailed);
            Assert.Equal("WebView2 탐색 시간이 초과되었습니다.", UiText.WebViewNavigationTimedOut);
            Assert.Equal("WebView2 탐색에 실패했습니다.", UiText.WebViewNavigationFailed);
            Assert.Equal("WebView2 요청 시간이 초과되었습니다.", UiText.WebViewRequestTimedOut);
            Assert.Equal("WebView2 요청에 실패했습니다.", UiText.WebViewRequestFailed);
            Assert.Equal("WebView2 대체 경로", UiText.TransportWebView);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void NewInstall_KoreanCulture_DefaultsToKorean()
    {
        var korean = AppSettings.CreateNewInstall(CultureInfo.GetCultureInfo("ko-KR"));
        Assert.Equal(UiLanguage.Korean, korean.UiLanguage);

        var english = AppSettings.CreateNewInstall(CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(UiLanguage.English, english.UiLanguage);

        var defaults = AppSettings.CreateDefaults();
        Assert.Equal(UiLanguage.English, defaults.UiLanguage);
    }

    [Fact]
    public void EstimatedReset_DoesNotLookAuthoritative()
    {
        var snapshot = new QuotaSnapshot
        {
            ResetAt = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7))),
            ResetAnchorSource = ResetAnchorSource.Default,
            ResetEstimated = true
        };

        UiText.SetLanguage(UiLanguage.English);
        var english = DisplayFormatting.ResetDisplay(snapshot);
        Assert.Equal("Reset time", english.TimeLabel);
        Assert.Equal("Not confirmed", english.TimeValue);
        Assert.Null(english.EstimateLabel);
        Assert.Null(english.EstimateValue);
        Assert.DoesNotContain("%", DisplayFormatting.CoverageFlyoutValue(snapshot), StringComparison.Ordinal);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = DisplayFormatting.ResetDisplay(snapshot);
            Assert.Equal("리셋 시각", korean.TimeLabel);
            Assert.Equal("확인되지 않음", korean.TimeValue);
            Assert.Null(korean.EstimateLabel);
            Assert.Null(korean.EstimateValue);
            Assert.Equal("지금 동기화", UiText.SyncNow);
            Assert.Equal("데이터 상태", UiText.DataStatus);
            Assert.Equal("확인되지 않음", DisplayFormatting.ReasoningLimitValue(null));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void ServerReset_IdentifiesServerSource()
    {
        var snapshot = new QuotaSnapshot
        {
            ResetAt = new DateTimeOffset(2026, 9, 7, 3, 21, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7, 3, 21, 0))),
            ResetAnchorSource = ResetAnchorSource.Server
        };
        var display = DisplayFormatting.ResetDisplay(snapshot);
        Assert.Contains("server", display.TimeValue, StringComparison.OrdinalIgnoreCase);
        Assert.Null(display.EstimateValue);
    }

    [Fact]
    public void UserConfiguredReset_IdentifiesUserSource()
    {
        var snapshot = new QuotaSnapshot
        {
            ResetAt = new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 7))),
            ResetAnchorSource = ResetAnchorSource.UserConfigured
        };
        var display = DisplayFormatting.ResetDisplay(snapshot);
        Assert.Contains("user configured", display.TimeValue, StringComparison.OrdinalIgnoreCase);
        Assert.Null(display.EstimateValue);
    }

    [Fact]
    public void CoverageOverall_IsSemanticNotPercent()
    {
        var partial = new CoverageInfo
        {
            NormalChats = true,
            NormalIndexState = CollectionState.Complete,
            ArchivedIndexState = CollectionState.Complete,
            ProjectsIndexState = CollectionState.Complete,
            IndexIncomplete = true,
            FailedConversations = 2,
            ConversationIncomplete = true
        };
        Assert.Equal(CollectionState.Partial, partial.OverallState);
        Assert.Equal("Partial", DisplayFormatting.OverallCollectionLabel(partial));
        Assert.DoesNotContain("%", DisplayFormatting.OverallCollectionLabel(partial), StringComparison.Ordinal);
        Assert.True(partial.ApproximatePercent < 50);

        var unavailable = new CoverageInfo { HistoryLoadedWithoutUsage = true };
        Assert.Equal(CollectionState.Unavailable, unavailable.OverallState);

        var estimated = new CoverageInfo
        {
            NormalChats = true,
            NormalIndexState = CollectionState.Complete,
            ArchivedIndexState = CollectionState.Complete,
            ProjectsIndexState = CollectionState.Complete,
            CountConfidence = CoverageConfidence.Estimated,
            ResetConfidence = CoverageConfidence.Estimated
        };
        Assert.Equal(CollectionState.Estimated, estimated.OverallState);

        var syncing = new QuotaSnapshot
        {
            IsSyncing = true,
            LastSync = null,
            Coverage = new CoverageInfo()
        };
        Assert.Equal("Syncing...", DisplayFormatting.CoverageFlyoutValue(syncing));
        Assert.DoesNotContain("0%", DisplayFormatting.CoverageFlyoutValue(syncing), StringComparison.Ordinal);
    }

    [Fact]
    public void SyncingSnapshot_KeepsPreviousCountAndDoesNotShowUnknownLimit()
    {
        var snapshot = new QuotaSnapshot
        {
            Used = 28,
            Limit = 50,
            ReconstructedUsed = 28,
            LastSync = DateTimeOffset.UtcNow,
            IsSyncing = true,
            Coverage = new CoverageInfo
            {
                NormalIndexState = CollectionState.Complete,
                ArchivedIndexState = CollectionState.Complete,
                ProjectsIndexState = CollectionState.Complete,
                CountConfidence = CoverageConfidence.Estimated,
                ResetConfidence = CoverageConfidence.Estimated
            },
            Reasoning = new ReasoningStats { Today = 3, ThisWeek = 10, Limit = null }
        };
        Assert.Equal(UiText.ReconstructedCount(28), DisplayFormatting.UsageLabel(snapshot));
        Assert.Contains("Previous data", DisplayFormatting.CountSourceLabel(snapshot), StringComparison.Ordinal);
        Assert.Contains("Syncing...", DisplayFormatting.StatusLabel(snapshot), StringComparison.Ordinal);
        Assert.Equal("Not available", DisplayFormatting.ReasoningLimitValue(snapshot.Reasoning.Limit));
        Assert.DoesNotContain("Unknown", DisplayFormatting.ReasoningLimitValue(snapshot.Reasoning.Limit), StringComparison.Ordinal);
        Assert.DoesNotContain("%", DisplayFormatting.CoverageFlyoutValue(snapshot), StringComparison.Ordinal);
    }
}
