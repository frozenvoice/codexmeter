using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class CompanionBridgeFailureTests
{
    [Theory]
    [InlineData(CompanionBridgeProtocol.NotConnectedError)]
    [InlineData(CompanionBridgeProtocol.DisconnectedError)]
    public void CompanionNotConnected_IsNotOffline(string error)
    {
        var response = new ProviderResponse { Status = 0, Error = error };
        var ex = new ChatGptProviderException(error, 0);
        Assert.True(response.IsCompanionDisconnected);
        Assert.True(ex.IsCompanionDisconnected);
        Assert.False(response.IsOffline);
        Assert.False(ex.IsOffline);
        Assert.False(response.IsNetworkUnavailable);
        Assert.False(ex.IsNetworkUnavailable);
        Assert.Equal("CompanionDisconnected", SyncFailureClassifier.Classify(ex).Category);
    }

    [Fact]
    public void BridgeTimeout_IsNotOffline()
    {
        var response = new ProviderResponse { Status = 0, Error = CompanionBridgeProtocol.TimeoutError };
        var ex = new ChatGptProviderException(CompanionBridgeProtocol.TimeoutError, 0);
        Assert.True(response.IsBridgeTimeout);
        Assert.True(ex.IsBridgeTimeout);
        Assert.False(response.IsOffline);
        Assert.False(ex.IsOffline);
        Assert.Equal("BridgeTimeout", SyncFailureClassifier.Classify(ex).Category);
    }

    [Fact]
    public void BridgeWriteFailed_IsNotOffline()
    {
        var response = new ProviderResponse { Status = 0, Error = CompanionBridgeProtocol.WriteFailedError };
        var ex = new ChatGptProviderException(CompanionBridgeProtocol.WriteFailedError, 0);
        Assert.True(response.IsBridgeWriteFailed);
        Assert.True(ex.IsBridgeWriteFailed);
        Assert.False(response.IsOffline);
        Assert.False(ex.IsOffline);
        Assert.Equal("BridgeWriteFailed", SyncFailureClassifier.Classify(ex).Category);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("Failed to fetch")]
    public void GenuineNetworkFailure_MapsToNetworkUnavailable(string error)
    {
        var response = new ProviderResponse { Status = 0, Error = error };
        var ex = new ChatGptProviderException(error, 0);
        Assert.True(response.IsNetworkUnavailable);
        Assert.True(response.IsOffline);
        Assert.True(ex.IsOffline);
        Assert.False(response.IsCompanionDisconnected);
        Assert.False(response.IsBridgeTimeout);
        Assert.False(response.IsBridgeWriteFailed);
    }

    [Fact]
    public void ExistingStatusClassifications_RemainIntact()
    {
        Assert.True(new ChatGptProviderException("Authentication required.", 401).IsUnauthorized);
        Assert.True(new ChatGptProviderException(CompanionDiagnostics.Forbidden403, 403).IsForbidden);
        Assert.True(new ChatGptProviderException("Rate limited.", 429).IsRateLimited);
        Assert.True(new ChatGptProviderException(CompanionDiagnostics.NoChatGptTab, 0).IsChatGptTabRequired);
        Assert.True(new ChatGptProviderException(CompanionDiagnostics.PageBridgeUnavailable, 0).IsPageBridgeUnavailable);
        Assert.True(new ChatGptProviderException("Provider schema mismatch", 0, schemaMismatch: true).SchemaMismatch);
        Assert.False(new ChatGptProviderException(CompanionDiagnostics.NoChatGptTab, 0).IsOffline);
        Assert.False(new ChatGptProviderException(CompanionDiagnostics.PageBridgeUnavailable, 0).IsOffline);
        Assert.False(new ChatGptProviderException("Provider schema mismatch", 0, schemaMismatch: true).IsOffline);
    }

    [Theory]
    [InlineData(CompanionBridgeProtocol.NotConnectedError)]
    [InlineData(CompanionBridgeProtocol.DisconnectedError)]
    [InlineData(CompanionBridgeProtocol.TimeoutError)]
    [InlineData(CompanionBridgeProtocol.WriteFailedError)]
    public async Task ProviderThrowsLocalBridgeCategory_NotOffline(string error)
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 0,
            Error = error
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(
            () => new ChatGptProvider(transport).GetModelCatalogAsync());
        Assert.False(ex.IsOffline);
        Assert.Equal(
            BridgeFailureClassification.IsCompanionDisconnected(error),
            ex.IsCompanionDisconnected);
        Assert.Equal(BridgeFailureClassification.IsBridgeTimeout(error), ex.IsBridgeTimeout);
        Assert.Equal(BridgeFailureClassification.IsBridgeWriteFailed(error), ex.IsBridgeWriteFailed);
    }

    [Fact]
    public async Task ProviderGenuineNetworkFailure_RemainsOffline()
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 0,
            Error = "Failed to fetch"
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(
            () => new ChatGptProvider(transport).GetModelCatalogAsync());
        Assert.True(ex.IsOffline);
        Assert.True(ex.IsNetworkUnavailable);
    }

    [Fact]
    public void LocalBridgeStatusLabels_AreExplicitAndNotOffline()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("Browser Companion disconnected.", UiText.CompanionDisconnectedStatus);
            Assert.Equal("Browser Companion timed out.", UiText.BridgeTimeoutStatus);
            Assert.Equal("Could not send the request to Browser Companion.", UiText.BridgeWriteFailedStatus);
            Assert.Equal(UiText.CompanionDisconnectedStatus, DisplayFormatting.StatusLabel(AppSyncStatus.CompanionDisconnected));
            Assert.Equal(UiText.BridgeTimeoutStatus, DisplayFormatting.StatusLabel(AppSyncStatus.BridgeTimeout));
            Assert.Equal(UiText.BridgeWriteFailedStatus, DisplayFormatting.StatusLabel(AppSyncStatus.BridgeWriteFailed));
            Assert.NotEqual(UiText.Offline, DisplayFormatting.StatusLabel(AppSyncStatus.CompanionDisconnected));
            Assert.NotEqual(UiText.ChatGptUnreachable, DisplayFormatting.StatusLabel(AppSyncStatus.CompanionDisconnected));

            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("Browser Companion 연결이 끊어졌습니다.", UiText.CompanionDisconnectedStatus);
            Assert.Equal("Browser Companion 응답 시간이 초과되었습니다.", UiText.BridgeTimeoutStatus);
            Assert.Equal("Browser Companion으로 요청을 보내지 못했습니다.", UiText.BridgeWriteFailedStatus);
            foreach (var status in new[]
            {
                AppSyncStatus.CompanionDisconnected,
                AppSyncStatus.BridgeTimeout,
                AppSyncStatus.BridgeWriteFailed
            })
            {
                var label = DisplayFormatting.StatusLabel(status);
                Assert.DoesNotContain("인터넷 연결 없음", label, StringComparison.Ordinal);
                Assert.DoesNotContain("ChatGPT에 연결할 수 없음", label, StringComparison.Ordinal);
                Assert.DoesNotContain(UiText.Offline, label, StringComparison.Ordinal);
                Assert.DoesNotContain(UiText.ChatGptUnreachable, label, StringComparison.Ordinal);
            }
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void AppDoesNotWireRetiredCompanionTransport()
    {
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.DoesNotContain("new BrowserCompanionTransport", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new WebViewTransport", app, StringComparison.Ordinal);
        Assert.Contains("new CodexAppServerClient()", app, StringComparison.Ordinal);
    }

    private static int ProMeterCanonicalPageBridgeVersion()
    {
        var canonical = File.ReadAllText(Find("extension/canonical.js"));
        var match = System.Text.RegularExpressions.Regex.Match(canonical, @"PAGE_BRIDGE_VERSION\s*=\s*(\d+)");
        Assert.True(match.Success);
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
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
