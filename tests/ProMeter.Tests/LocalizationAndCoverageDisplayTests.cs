using System.Globalization;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class LocalizationAndCoverageDisplayTests
{
    [Fact]
    public void WebViewDiagnosticAndVerificationStrings_ExistInEnglishAndKorean()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("Test WebView2", UiText.TestWebView2);
            Assert.Equal("Testing WebView2 connection...", UiText.TestingWebView2);
            Assert.Contains("WebView2 works on this PC/account.", UiText.WebViewDiagnosticPass, StringComparison.Ordinal);
            Assert.Contains("FAIL_AUTH", UiText.WebViewDiagnosticFailAuth, StringComparison.Ordinal);
            Assert.Contains("FAIL_SESSION", UiText.WebViewDiagnosticFailSession, StringComparison.Ordinal);
            Assert.Contains("FAIL_API", UiText.WebViewDiagnosticFailApi, StringComparison.Ordinal);
            Assert.Contains("HTTP 403", UiText.WebViewDiagnosticForbidden, StringComparison.Ordinal);
            Assert.Contains("Current connection settings were not changed", UiText.WebViewDiagnosticCancelled, StringComparison.Ordinal);
            Assert.Equal("Run full WebView2 verification sync", UiText.RunFullWebViewVerification);
            Assert.Equal("Browser Companion count: 7", UiText.BrowserCompanionCount(7));
            Assert.Equal("WebView2 count: 6", UiText.WebViewCount(6));
            Assert.Equal("Difference: -1", UiText.VerificationDifference(-1));
            Assert.Equal("Use WebView2 as default connection", UiText.UseWebViewAsDefault);
            Assert.Contains("explicit confirmation", UiText.UseWebViewAsDefaultFailed, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                "conversation index: HTTP 500; schema mismatch",
                UiText.WebViewDiagnosticTechnical("conversation-index", 500, "schema-mismatch"));

            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("WebView2 연결 테스트", UiText.TestWebView2);
            Assert.Equal("WebView2 연결 확인 중...", UiText.TestingWebView2);
            Assert.Contains("이 PC와 계정에서 정상 동작합니다", UiText.WebViewDiagnosticPass, StringComparison.Ordinal);
            Assert.Contains("로그인 방식은 WebView2에서 지원되지 않거나", UiText.WebViewDiagnosticFailAuth, StringComparison.Ordinal);
            Assert.Contains("ChatGPT 세션을 확인하지 못했습니다", UiText.WebViewDiagnosticFailSession, StringComparison.Ordinal);
            Assert.Contains("ChatGPT 기록 API를 사용할 수 없습니다", UiText.WebViewDiagnosticFailApi, StringComparison.Ordinal);
            Assert.Contains("HTTP 403", UiText.WebViewDiagnosticForbidden, StringComparison.Ordinal);
            Assert.Contains("현재 연결 설정은 변경되지 않았습니다", UiText.WebViewDiagnosticCancelled, StringComparison.Ordinal);
            Assert.Equal("WebView2 전체 검증 동기화", UiText.RunFullWebViewVerification);
            Assert.Equal("Browser Companion 사용량: 7", UiText.BrowserCompanionCount(7));
            Assert.Equal("WebView2 사용량: 6", UiText.WebViewCount(6));
            Assert.Equal("차이: -1", UiText.VerificationDifference(-1));
            Assert.Equal("WebView2를 기본 연결로 사용", UiText.UseWebViewAsDefault);
            Assert.Equal(
                "대화 목록: HTTP 500; 응답 형식 불일치",
                UiText.WebViewDiagnosticTechnical("conversation-index", 500, "schema-mismatch"));
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
        Assert.Equal("Estimate", english.EstimateLabel);
        Assert.Contains("Sep 7 00:00", english.EstimateValue, StringComparison.Ordinal);
        Assert.DoesNotContain("estimated reset", english.TimeValue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("%", DisplayFormatting.CoverageFlyoutValue(snapshot), StringComparison.Ordinal);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = DisplayFormatting.ResetDisplay(snapshot);
            Assert.Equal("리셋 시각", korean.TimeLabel);
            Assert.Equal("확인되지 않음", korean.TimeValue);
            Assert.Equal("추정 기준", korean.EstimateLabel);
            Assert.Contains("9월 7일 00:00", korean.EstimateValue, StringComparison.Ordinal);
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
        Assert.Equal("28 / 50", DisplayFormatting.UsageLabel(snapshot));
        Assert.Contains("Previous data", DisplayFormatting.CountSourceLabel(snapshot), StringComparison.Ordinal);
        Assert.Contains("Syncing...", DisplayFormatting.StatusLabel(snapshot), StringComparison.Ordinal);
        Assert.Equal("Not available", DisplayFormatting.ReasoningLimitValue(snapshot.Reasoning.Limit));
        Assert.DoesNotContain("Unknown", DisplayFormatting.ReasoningLimitValue(snapshot.Reasoning.Limit), StringComparison.Ordinal);
        Assert.DoesNotContain("%", DisplayFormatting.CoverageFlyoutValue(snapshot), StringComparison.Ordinal);
    }
}
