using System.Text.Json;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class WebViewInitializationLifecycleTests
{
    [Fact]
    public void HostMustBeRealizedBeforeFirstNavigation()
    {
        var unrealized = WebViewInitializationCoordinator.RequireHostBeforeNavigation(false);
        Assert.False(unrealized.Success);
        Assert.Equal(WebViewInitializationFailure.HostNotRealized, unrealized.Failure);

        var host = new WebViewHostSession();
        Assert.False(host.IsRealized);
        Assert.Equal(
            WebViewInitializationFailure.HostNotRealized,
            WebViewInitializationCoordinator.RequireHostBeforeNavigation(host.IsRealized).Failure);

        host.RealizeForDiagnostic();
        Assert.True(host.IsRealized);
        Assert.True(host.IsVisible);
        Assert.True(WebViewInitializationCoordinator.RequireHostBeforeNavigation(host.IsRealized).Success);
        Assert.Throws<InvalidOperationException>(() => new WebViewHostSession().ShowLoginOnSameHost());
    }

    [Fact]
    public async Task InitializationTimeout_ReturnsDeterministicFailure()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var outcome = await coordinator.RunAsync(
            async ct =>
            {
                await WebViewBoundedWait.WaitAsync(Task.Delay(Timeout.Infinite, ct), TimeSpan.FromMilliseconds(40), ct);
                return WebViewInitializationOutcome.Succeeded();
            },
            CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(WebViewInitializationFailure.Timeout, outcome.Failure);
        Assert.Equal(WebViewDiagnosticStages.InitializeTimeout, outcome.Stage);
        Assert.Equal(WebViewInitializationStatus.Failed, coordinator.Status);
    }

    [Fact]
    public async Task NavigationTimeout_ReturnsDeterministicFailure()
    {
        await Assert.ThrowsAsync<WebViewInitializationException>(() =>
            WebViewBoundedWait.WaitNavigationAsync(
                Task.Delay(Timeout.Infinite),
                TimeSpan.FromMilliseconds(40),
                CancellationToken.None));

        var coordinator = new WebViewInitializationCoordinator();
        var outcome = await coordinator.RunAsync(
            _ => Task.FromResult(WebViewInitializationOutcome.NavigationTimedOut()),
            CancellationToken.None);

        Assert.Equal(WebViewInitializationFailure.NavigationTimeout, outcome.Failure);
        Assert.Equal(WebViewDiagnosticStages.NavigationTimeout, outcome.Stage);
        Assert.Equal(WebViewInitializationStatus.Failed, coordinator.Status);
    }

    [Fact]
    public async Task Cancellation_ReturnsWithoutCorruptingOtherWaiters()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<WebViewInitializationOutcome> Stages(CancellationToken ct)
        {
            started.TrySetResult();
            await Task.Delay(200, ct);
            return WebViewInitializationOutcome.Succeeded();
        }

        using var cancelledWaiter = new CancellationTokenSource();
        var first = coordinator.RunAsync(Stages, cancelledWaiter.Token);
        await started.Task;
        var second = coordinator.RunAsync(Stages, CancellationToken.None);
        cancelledWaiter.Cancel();

        var cancelled = await first;
        var succeeded = await second;

        Assert.Equal(WebViewInitializationFailure.Cancelled, cancelled.Failure);
        Assert.True(succeeded.Success);
        Assert.Equal(WebViewInitializationStatus.Ready, coordinator.Status);
        Assert.Equal(1, coordinator.SharedStartCount);
        Assert.Equal(0, coordinator.WaiterCount);
    }

    [Fact]
    public async Task ConcurrentInitialization_IsSingleFlight()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runs = 0;

        async Task<WebViewInitializationOutcome> Stages(CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return WebViewInitializationOutcome.Succeeded();
        }

        var first = coordinator.RunAsync(Stages, CancellationToken.None);
        await entered.Task;
        var second = coordinator.RunAsync(Stages, CancellationToken.None);
        release.SetResult();

        Assert.True((await first).Success);
        Assert.True((await second).Success);
        Assert.Equal(1, runs);
        Assert.Equal(1, coordinator.SharedStartCount);
        Assert.Equal(WebViewInitializationStatus.Ready, coordinator.Status);
    }

    [Fact]
    public async Task FailedInitialization_AllowsControlledRetry()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var attempts = 0;

        Task<WebViewInitializationOutcome> Stages(CancellationToken _)
        {
            attempts++;
            return Task.FromResult(
                attempts == 1
                    ? WebViewInitializationOutcome.TimedOut()
                    : WebViewInitializationOutcome.Succeeded());
        }

        var first = await coordinator.RunAsync(Stages, CancellationToken.None);
        var second = await coordinator.RunAsync(Stages, CancellationToken.None);

        Assert.False(first.Success);
        Assert.True(second.Success);
        Assert.Equal(2, attempts);
        Assert.Equal(2, coordinator.SharedStartCount);
        Assert.Equal(WebViewInitializationStatus.Ready, coordinator.Status);
    }

    [Fact]
    public async Task ReadyCoordinator_ReusesWithoutRerunningStages()
    {
        var coordinator = new WebViewInitializationCoordinator();
        var runs = 0;
        Task<WebViewInitializationOutcome> Stages(CancellationToken _)
        {
            runs++;
            return Task.FromResult(WebViewInitializationOutcome.Succeeded());
        }

        Assert.True((await coordinator.RunAsync(Stages, CancellationToken.None)).Success);
        Assert.True((await coordinator.RunAsync(Stages, CancellationToken.None)).Success);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task SettingsCancellation_ReleasesRunningStateAndHidesHost()
    {
        var gate = new WebViewOperationGate();
        var host = new WebViewHostSession();
        using var settingsClosed = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = gate.RunAsync(
            async ct =>
            {
                host.RealizeForDiagnostic();
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Pass);
                }
                catch (OperationCanceledException)
                {
                    host.Cancel();
                    return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Cancelled);
                }
            },
            () => new WebViewDiagnosticResult(WebViewDiagnosticStatus.FailApi),
            settingsClosed.Token);

        await started.Task;
        Assert.True(gate.IsRunning);
        Assert.True(host.IsVisible);

        settingsClosed.Cancel();
        var result = await run;

        Assert.Equal(WebViewDiagnosticStatus.Cancelled, result.Status);
        Assert.False(gate.IsRunning);
        Assert.False(host.IsVisible);
        Assert.False(host.CheckingOverlayVisible);
        Assert.False(host.LoginSurfaceVisible);
    }

    [Fact]
    public async Task SuccessfulExistingSession_HidesDiagnosticHost()
    {
        var host = new RecordingHostLogin();
        var transport = PassingTransport();

        var result = await new WebViewCompatibilityDiagnostic(transport, host, host).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Pass, result.Status);
        Assert.Equal(1, host.Session.HostInstanceId);
        Assert.Equal(1, host.RealizeCount);
        Assert.Equal(0, host.PrepareLoginCount);
        Assert.Equal(0, host.ShowCount);
        Assert.False(host.Session.IsVisible);
        Assert.False(host.Session.CheckingOverlayVisible);
    }

    [Fact]
    public async Task LoginRequired_ReusesTheSameRealizedHost()
    {
        var host = new RecordingHostLogin();
        var transport = PassingTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 401 },
            SignedInSession());

        var result = await new WebViewCompatibilityDiagnostic(transport, host, host).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Pass, result.Status);
        Assert.Equal(1, host.ShowCount);
        Assert.Equal(1, host.RealizeCount);
        Assert.Equal(1, host.PrepareLoginCount);
        Assert.Equal(1, host.Session.HostInstanceId);
        Assert.False(host.Session.IsVisible);
    }

    [Fact]
    public async Task InitializationTimeout_ReturnsFailApiWithLocalizedDetailAndLeavesCompanionUntouched()
    {
        var directory = NewTempDirectory();
        using var store = new SqliteStore(Path.Combine(directory, "production.db"));
        store.SetState("last_index_sync", "watermark-before");
        var settings = AppSettings.CreateDefaults();
        settings.AuthTransport = AuthTransportKind.BrowserCompanion;
        settings.AutoSync = false;
        settings.StartWithWindows = false;
        var settingsBefore = JsonSerializer.Serialize(settings);
        var host = new RecordingHostLogin();
        var transport = new ScriptedTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.Timeout });

        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var result = await new WebViewCompatibilityDiagnostic(transport, host, host).RunAsync();

            Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
            Assert.Equal(UiText.WebViewInitializationTimedOut, result.TechnicalDetail);
            Assert.Equal("WebView2 initialization timed out.", result.TechnicalDetail);
            Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
            Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
            Assert.Equal("watermark-before", store.GetState("last_index_sync"));
            Assert.False(host.Session.IsVisible);

            UiText.SetLanguage(UiLanguage.Korean);
            var korean = WebViewCompatibilityDiagnostic.MapInitializationFailure(
                new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.Timeout });
            Assert.Equal("WebView2 초기화 시간이 초과되었습니다.", korean?.TechnicalDetail);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public async Task RuntimeUnavailable_ReturnsSafeDiagnosticFailure()
    {
        var host = new RecordingHostLogin();
        var transport = new ScriptedTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 0, Error = WebViewInitializationCodes.RuntimeUnavailable });

        var result = await new WebViewCompatibilityDiagnostic(transport, host, host).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
        Assert.Equal(UiText.WebViewInitializationFailed, result.TechnicalDetail);
        Assert.False(host.Session.IsVisible);
    }

    [Fact]
    public async Task DiagnosticCancellation_LogsCancelledAndReleasesHost()
    {
        var host = new RecordingHostLogin();
        var transport = new ScriptedTransport { Hold = true };
        transport.SetQueue(ChatGptEndpoints.Session, SignedInSession());
        using var cts = new CancellationTokenSource();
        var run = new WebViewCompatibilityDiagnostic(transport, host, host).RunAsync(cts.Token);
        await transport.Entered.Task;
        cts.Cancel();

        var result = await run;
        Assert.Equal(WebViewDiagnosticStatus.Cancelled, result.Status);
        Assert.False(host.Session.IsVisible);
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

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingHostLogin : IWebViewInteractiveLogin, IWebViewDiagnosticHost
    {
        public WebViewHostSession Session { get; } = new();
        public int RealizeCount { get; private set; }
        public int PrepareLoginCount { get; private set; }
        public int ShowCount { get; private set; }

        public void RealizeDiagnosticHost()
        {
            RealizeCount++;
            Session.RealizeForDiagnostic();
        }

        public void HideDiagnosticHost() => Session.HideAfterSuccessfulSession();

        public void PrepareLoginOnSameHost()
        {
            PrepareLoginCount++;
            Session.ShowLoginOnSameHost();
        }

        public Task<WebViewInteractiveLoginResult> ShowInteractiveLoginAsync(
            CancellationToken cancellationToken = default)
        {
            ShowCount++;
            Session.ShowLoginOnSameHost();
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
