using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class WebViewRequestAndNavigationTests
{
    [Fact]
    public async Task RequestTimeout_ReturnsFailApiWithRequestSpecificText()
    {
        var host = new RecordingHost();
        var transport = new ScriptedTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.RequestTimeout });
        var stages = new List<string>();

        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var result = await new WebViewCompatibilityDiagnostic(
                transport,
                host,
                host,
                stages.Add).RunAsync();

            Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
            Assert.Equal("WebView2 request timed out.", result.TechnicalDetail);
            Assert.NotEqual(UiText.WebViewInitializationTimedOut, result.TechnicalDetail);
            Assert.Contains(WebViewDiagnosticStages.RequestTimeoutFor("session"), stages);
            Assert.DoesNotContain(stages, stage => stage == WebViewDiagnosticStages.InitializeTimeout);

            UiText.SetLanguage(UiLanguage.Korean);
            var korean = WebViewCompatibilityDiagnostic.MapInitializationFailure(
                new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.RequestTimeout });
            Assert.Equal("WebView2 요청 시간이 초과되었습니다.", korean?.TechnicalDetail);
            Assert.NotEqual(UiText.WebViewInitializationTimedOut, korean?.TechnicalDetail);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void NavigationAndRequestFailures_AreDistinctFromInitializationTimeout()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var navigationTimeout = WebViewCompatibilityDiagnostic.MapInitializationFailure(
                new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.NavigationTimeout });
            var navigationFailed = WebViewCompatibilityDiagnostic.MapInitializationFailure(
                new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.NavigationFailed });
            var requestTimeout = WebViewCompatibilityDiagnostic.MapInitializationFailure(
                new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.RequestTimeout });
            var scriptTimeout = WebViewCompatibilityDiagnostic.MapInitializationFailure(
                new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.ScriptExecutionTimeout });
            var initTimeout = WebViewCompatibilityDiagnostic.MapInitializationFailure(
                new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.Timeout });

            Assert.Equal(WebViewDiagnosticStatus.FailApi, navigationTimeout?.Status);
            Assert.Equal("WebView2 navigation timed out.", navigationTimeout?.TechnicalDetail);
            Assert.Equal("WebView2 navigation failed.", navigationFailed?.TechnicalDetail);
            Assert.Equal("WebView2 request timed out.", requestTimeout?.TechnicalDetail);
            Assert.Equal("WebView2 request timed out.", scriptTimeout?.TechnicalDetail);
            Assert.Equal("WebView2 initialization timed out.", initTimeout?.TechnicalDetail);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public async Task CancelledRequest_ReturnsPromptlyAndReleasesGate()
    {
        var gate = new WebViewOperationGate();
        using var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();

        var run = gate.RunAsync(
            async ct =>
            {
                started.SetResult();
                try
                {
                    await WebViewBoundedWait.WaitRequestAsync(
                        Task.Delay(Timeout.Infinite, CancellationToken.None),
                        TimeSpan.FromSeconds(25),
                        ct);
                    return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Pass);
                }
                catch (OperationCanceledException)
                {
                    return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Cancelled);
                }
            },
            () => new WebViewDiagnosticResult(WebViewDiagnosticStatus.FailApi),
            cts.Token);

        await started.Task;
        Assert.True(gate.IsRunning);
        cts.Cancel();
        var result = await run;
        clock.Stop();

        Assert.Equal(WebViewDiagnosticStatus.Cancelled, result.Status);
        Assert.False(gate.IsRunning);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TimedOutRequest_ReleasesGateAndAllowsALaterDiagnostic()
    {
        var gate = new WebViewOperationGate();
        var host = new RecordingHost();
        var timedOut = new ScriptedTransport();
        timedOut.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.RequestTimeout });

        var first = await gate.RunAsync(
            ct => new WebViewCompatibilityDiagnostic(timedOut, host, host).RunAsync(ct),
            () => new WebViewDiagnosticResult(WebViewDiagnosticStatus.FailApi),
            CancellationToken.None);

        Assert.Equal(WebViewDiagnosticStatus.FailApi, first.Status);
        Assert.Equal(UiText.WebViewRequestTimedOut, first.TechnicalDetail);
        Assert.False(gate.IsRunning);

        var second = await gate.RunAsync(
            ct => new WebViewCompatibilityDiagnostic(PassingTransport(), host, host).RunAsync(ct),
            () => new WebViewDiagnosticResult(WebViewDiagnosticStatus.FailApi),
            CancellationToken.None);

        Assert.Equal(WebViewDiagnosticStatus.Pass, second.Status);
        Assert.False(gate.IsRunning);
    }

    [Fact]
    public async Task CancelledDiagnostic_ReleasesGateAndAllowsALaterDiagnostic()
    {
        var gate = new WebViewOperationGate();
        var host = new RecordingHost();
        var holding = new ScriptedTransport { Hold = true };
        holding.SetQueue(ChatGptEndpoints.Session, SignedInSession());
        using var cts = new CancellationTokenSource();

        var first = gate.RunAsync(
            ct => new WebViewCompatibilityDiagnostic(holding, host, host).RunAsync(ct),
            () => new WebViewDiagnosticResult(WebViewDiagnosticStatus.FailApi),
            cts.Token);
        await holding.Entered.Task;
        cts.Cancel();

        Assert.Equal(WebViewDiagnosticStatus.Cancelled, (await first).Status);
        Assert.False(gate.IsRunning);

        var second = await gate.RunAsync(
            ct => new WebViewCompatibilityDiagnostic(PassingTransport(), host, host).RunAsync(ct),
            () => new WebViewDiagnosticResult(WebViewDiagnosticStatus.FailApi),
            CancellationToken.None);

        Assert.Equal(WebViewDiagnosticStatus.Pass, second.Status);
        Assert.False(gate.IsRunning);
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
    public async Task TimeoutDiagnostics_ContainNoUserContentOrSecrets()
    {
        const string secret = "user-prompt-and-cookie-value";
        var host = new RecordingHost();
        var transport = new ScriptedTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse
            {
                Status = 0,
                Error = WebViewInitializationCodes.RequestTimeout,
                Body = $"{{\"prompt\":\"{secret}\",\"assistant\":\"{secret}\",\"Authorization\":\"Bearer {secret}\"}}"
            });

        var result = await new WebViewCompatibilityDiagnostic(transport, host, host).RunAsync();
        var serialized = JsonSerializer.Serialize(result);

        Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assistant", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chatgpt.com", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequestTimeout_LeavesCompanionSettingsAndWatermarksUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var store = new SqliteStore(Path.Combine(directory, "production.db"));
        store.SetState("last_index_sync", "watermark-before");
        var settings = AppSettings.CreateDefaults();
        settings.AuthTransport = AuthTransportKind.BrowserCompanion;
        settings.AutoSync = false;
        settings.StartWithWindows = false;
        var before = JsonSerializer.Serialize(settings);
        var transport = new ScriptedTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.RequestTimeout });

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingHost(), new RecordingHost()).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
        Assert.False(settings.AutoSync);
        Assert.False(settings.StartWithWindows);
        Assert.Equal("watermark-before", store.GetState("last_index_sync"));
    }

    [Fact]
    public void FetchTimeoutConstants_MatchRequiredBounds()
    {
        Assert.Equal(20_000, WebViewFetchScript.RequestTimeoutMilliseconds);
        Assert.Equal(TimeSpan.FromSeconds(20), WebViewFetchScript.RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(25), WebViewFetchScript.ScriptGuardTimeout);
        Assert.True(WebViewFetchScript.ScriptGuardTimeout > WebViewFetchScript.RequestTimeout);
    }

    private static ScriptedTransport PassingTransport()
    {
        var transport = new ScriptedTransport();
        transport.SetQueue(ChatGptEndpoints.Session, SignedInSession());
        transport.SetQueue(ChatGptEndpoints.AccountsCheck, Ok("{\"accounts\":{}}"));
        transport.SetQueue(ChatGptEndpoints.Models, Ok("{\"models\":[]}"));
        transport.SetQueue(
            ChatGptEndpoints.ConversationsPage(0, ConversationIndexPager.RequestedLimit, archived: false),
            Ok("{\"items\":[],\"total\":0,\"offset\":0,\"limit\":100,\"has_more\":false}"));
        return transport;
    }

    private static ProviderResponse SignedInSession() => Ok("{\"user\":{\"id\":\"synthetic-user\"}}");

    private static ProviderResponse Ok(string body) => new() { Status = 200, Body = body };

    private sealed class RecordingHost : IWebViewInteractiveLogin, IWebViewDiagnosticHost
    {
        public WebViewHostSession Session { get; } = new();

        public void RealizeDiagnosticHost() => Session.RealizeForDiagnostic();
        public void HideDiagnosticHost() => Session.HideAfterSuccessfulSession();
        public void PrepareLoginOnSameHost() => Session.ShowLoginOnSameHost();

        public Task<WebViewInteractiveLoginResult> ShowInteractiveLoginAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WebViewInteractiveLoginResult.SignedIn);
        }
    }

    private sealed class ScriptedTransport : IChatGptTransport
    {
        private readonly Dictionary<string, Queue<ProviderResponse>> _responses = new(StringComparer.Ordinal);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Hold { get; set; }

        public void SetQueue(string path, params ProviderResponse[] responses) =>
            _responses[path] = new Queue<ProviderResponse>(responses);

        public async Task<ProviderResponse> SendAsync(
            string method,
            string path,
            string? jsonBody = null,
            CancellationToken cancellationToken = default)
        {
            _ = method;
            _ = jsonBody;
            Entered.TrySetResult();
            if (Hold)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_responses.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                return queue.Dequeue();
            }

            return new ProviderResponse { Status = 500, Error = "unexpected diagnostic operation" };
        }
    }
}
