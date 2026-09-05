using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class WebViewRequestAndNavigationTests
{
    [Fact]
    public void NavigationAndRequestFailures_AreDistinctFromInitializationTimeout()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            Assert.Equal("WebView2 navigation timed out.", UiText.WebViewNavigationTimedOut);
            Assert.Equal("WebView2 navigation failed.", UiText.WebViewNavigationFailed);
            Assert.Equal("WebView2 request timed out.", UiText.WebViewRequestTimedOut);
            Assert.Equal("WebView2 request failed.", UiText.WebViewRequestFailed);
            Assert.Equal("WebView2 initialization timed out.", UiText.WebViewInitializationTimedOut);
            Assert.NotEqual(UiText.WebViewInitializationTimedOut, UiText.WebViewRequestTimedOut);
            Assert.NotEqual(UiText.WebViewInitializationTimedOut, UiText.WebViewNavigationTimedOut);

            UiText.SetLanguage(UiLanguage.Korean);
            Assert.Equal("WebView2 요청 시간이 초과되었습니다.", UiText.WebViewRequestTimedOut);
            Assert.Equal("WebView2 초기화 시간이 초과되었습니다.", UiText.WebViewInitializationTimedOut);
            Assert.NotEqual(UiText.WebViewInitializationTimedOut, UiText.WebViewRequestTimedOut);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public async Task CancelledRequest_ReturnsPromptly()
    {
        using var cts = new CancellationTokenSource();
        var clock = Stopwatch.StartNew();
        var pending = WebViewBoundedWait.WaitRequestAsync(
            Task.Delay(Timeout.Infinite, CancellationToken.None),
            TimeSpan.FromSeconds(25),
            cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        clock.Stop();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void FetchScript_UsesAbortControllerAndSafeTimeoutCode()
    {
        const string secret = "secret-access-token";
        var script = WebViewFetchScript.Build("GET", ChatGptEndpoints.Session, null, secret);

        Assert.Contains("AbortController", script, StringComparison.Ordinal);
        Assert.Contains("signal: controller.signal", script, StringComparison.Ordinal);
        Assert.Contains("clearTimeout(timer)", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.Contains(WebViewFetchScript.RequestTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture), script, StringComparison.Ordinal);
        Assert.DoesNotContain("error.message", script, StringComparison.Ordinal);
        Assert.DoesNotContain("String(error", script, StringComparison.Ordinal);

        Assert.Equal(
            WebViewInitializationCodes.RequestTimeout,
            WebViewFetchScript.MapJavaScriptError("AbortError", "Authorization: Bearer " + secret));
        Assert.Equal(
            WebViewInitializationCodes.RequestFailed,
            WebViewFetchScript.MapJavaScriptError("TypeError", "Cookie=abc; prompt=hello"));
        Assert.DoesNotContain(secret, WebViewFetchScript.MapJavaScriptError("AbortError", secret), StringComparison.Ordinal);
    }

    [Fact]
    public void FetchScriptResult_DoesNotSurfaceRawSecrets()
    {
        const string secret = "Authorization: Bearer leaked-token; Cookie=abc; prompt=hidden";
        var mapped = WebViewFetchScript.MapScriptResult(
            $"{{\"status\":0,\"error\":\"{secret}\",\"body\":\"assistant said hi\"}}");

        Assert.Equal(WebViewInitializationCodes.RequestFailed, mapped.Error);
        Assert.Equal("", mapped.Body);
        var serialized = JsonSerializer.Serialize(mapped);
        Assert.DoesNotContain("leaked-token", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Cookie", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("assistant", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NavigationSuccess_IsDistinctFromFailure()
    {
        var wait = new WebViewNavigationWait();
        var (epoch, task) = wait.Begin();

        Assert.True(wait.TryComplete(epoch, isSuccess: true));
        var result = await task;
        Assert.True(result.Succeeded);
        Assert.Equal(WebViewDiagnosticStages.HomeNavigationComplete, result.Stage);
        Assert.False(wait.HandlerAttached);
    }

    [Fact]
    public async Task NavigationFailure_DoesNotCompleteSuccessfully()
    {
        var wait = new WebViewNavigationWait();
        var (epoch, task) = wait.Begin();

        Assert.True(wait.TryComplete(epoch, isSuccess: false, webErrorStatus: "Timeout"));
        var result = await task;
        Assert.False(result.Succeeded);
        Assert.Equal(WebViewInitializationFailure.NavigationFailed, result.Failure);
        Assert.Equal(WebViewDiagnosticStages.HomeNavigationFailed, result.Stage);
        Assert.Equal("Timeout", result.WebErrorStatus);
        Assert.NotEqual(WebViewDiagnosticStages.HomeNavigationComplete, result.Stage);
    }

    [Fact]
    public void UnsafeWebErrorStatus_IsDropped()
    {
        var sanitized = WebViewNavigationResult.SanitizeWebErrorStatus(
            "https://chatgpt.com/auth?token=abc&prompt=hi");
        Assert.Null(sanitized);
        Assert.Null(WebViewNavigationResult.Failed("https://example/?cookie=1").WebErrorStatus);
        Assert.Equal("ConnectionAborted", WebViewNavigationResult.Failed("ConnectionAborted").WebErrorStatus);
    }

    [Fact]
    public async Task NavigationTimeout_ReturnsNavigationTimeout()
    {
        var wait = new WebViewNavigationWait();
        var (_, task) = wait.Begin();

        var ex = await Assert.ThrowsAsync<WebViewInitializationException>(() =>
            WebViewBoundedWait.WaitNavigationAsync(task, TimeSpan.FromMilliseconds(40), CancellationToken.None));

        Assert.Equal(WebViewInitializationFailure.NavigationTimeout, ex.Failure);
        Assert.Equal(WebViewDiagnosticStages.NavigationTimeout, ex.Stage);
        wait.Invalidate();
        Assert.False(wait.HandlerAttached);
    }

    [Fact]
    public async Task NavigationCancellation_ReturnsCancelled()
    {
        var wait = new WebViewNavigationWait();
        var (_, task) = wait.Begin();
        using var cts = new CancellationTokenSource();
        var pending = WebViewBoundedWait.WaitNavigationAsync(task, TimeSpan.FromSeconds(20), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        wait.Invalidate();
        Assert.False(wait.HandlerAttached);
    }

    [Fact]
    public async Task LateNavigationCompleted_CannotFinishANewerWait()
    {
        var wait = new WebViewNavigationWait();
        var (firstEpoch, first) = wait.Begin();
        wait.Invalidate();
        var (secondEpoch, second) = wait.Begin();

        Assert.False(wait.TryComplete(firstEpoch, isSuccess: true));
        Assert.False(first.IsCompletedSuccessfully);
        Assert.False(second.IsCompleted);

        Assert.True(wait.TryComplete(secondEpoch, isSuccess: true));
        Assert.True((await second).Succeeded);
        Assert.False(wait.TryComplete(firstEpoch, isSuccess: true));
    }

    [Fact]
    public async Task NavigationFailure_AllowsRetry()
    {
        var wait = new WebViewNavigationWait();
        var (firstEpoch, first) = wait.Begin();
        Assert.True(wait.TryComplete(firstEpoch, isSuccess: false, webErrorStatus: "Unknown"));
        Assert.False((await first).Succeeded);

        var (secondEpoch, second) = wait.Begin();
        Assert.True(wait.TryComplete(secondEpoch, isSuccess: true));
        var retried = await second;
        Assert.True(retried.Succeeded);
        Assert.Equal(WebViewDiagnosticStages.HomeNavigationComplete, retried.Stage);
    }

    [Fact]
    public async Task ScriptGuardTimeout_IsNotInitializationTimeout()
    {
        var ex = await Assert.ThrowsAsync<WebViewInitializationException>(() =>
            WebViewBoundedWait.WaitRequestAsync(
                Task.Delay(Timeout.Infinite),
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None));

        Assert.Equal(WebViewInitializationFailure.ScriptExecutionTimeout, ex.Failure);
        Assert.NotEqual(WebViewInitializationFailure.Timeout, ex.Failure);
        Assert.Equal(WebViewDiagnosticStages.ScriptExecutionTimeout, ex.Stage);
    }

    [Fact]
    public void FetchTimeoutConstants_MatchRequiredBounds()
    {
        Assert.Equal(20_000, WebViewFetchScript.RequestTimeoutMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(20), WebViewFetchScript.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(25), WebViewFetchScript.ScriptGuardTimeout);
        Assert.True(WebViewFetchScript.ScriptGuardTimeout > WebViewFetchScript.RequestTimeout);
    }
}
