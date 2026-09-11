using CycleArc.Providers.ChatGpt;

namespace CycleArc.Tests;

public class CompanionReconnectGraceTests
{
    [Fact]
    public void DefaultGrace_IsShortAndBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), CompanionReconnectGrace.DefaultDuration);
        Assert.True(CompanionReconnectGrace.DefaultDuration >= TimeSpan.FromSeconds(5));
        Assert.True(CompanionReconnectGrace.DefaultDuration <= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DisconnectedCompanion_ThatReconnectsDuringGrace_ContinuesRequest()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(2) };
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var logs = new List<string>();
        var transport = new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromSeconds(2));
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        await WaitUntil(() => logs.Contains("sync waiting for companion reconnect"), TimeSpan.FromSeconds(1));
        var generation = hub.BeginConnection();
        Assert.True(hub.TryAcceptHello(generation, true));
        await WaitUntil(() => !string.IsNullOrWhiteSpace(requestId), TimeSpan.FromSeconds(1));
        Assert.True(hub.TryComplete(
            requestId!,
            new ProviderResponse { Status = 200, Body = """{"models":[]}""" },
            generation));
        var response = await send;
        Assert.True(response.IsSuccess);
        Assert.False(response.IsCompanionDisconnected);
        Assert.False(response.IsOffline);
    }

    [Fact]
    public async Task PersistentDisconnect_ReturnsCompanionDisconnectedPromptly()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(60) };
        var logs = new List<string>();
        var transport = new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromMilliseconds(200));
        var started = DateTime.UtcNow;
        var response = await transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        var elapsed = DateTime.UtcNow - started;
        Assert.True(response.IsCompanionDisconnected);
        Assert.False(response.IsOffline);
        Assert.Equal(CompanionBridgeProtocol.NotConnectedError, response.Error);
        Assert.True(elapsed < TimeSpan.FromSeconds(2), elapsed.ToString());
        Assert.Contains("sync waiting for companion reconnect", logs);
    }

    [Fact]
    public async Task Cancellation_InterruptsReconnectWait()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(60) };
        var transport = new BrowserCompanionTransport(hub, null, TimeSpan.FromSeconds(8));
        using var cts = new CancellationTokenSource();
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, cts.Token);
        await Task.Delay(30);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
    }

    [Fact]
    public async Task ReconnectGrace_IsBounded()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(60) };
        var transport = new BrowserCompanionTransport(hub, null, TimeSpan.FromMilliseconds(150));
        var started = DateTime.UtcNow;
        var response = await transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        Assert.True(response.IsCompanionDisconnected);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task AlreadyConnected_DoesNotWait()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(2) };
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var logs = new List<string>();
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var transport = new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromSeconds(8));
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        await WaitUntil(() => !string.IsNullOrWhiteSpace(requestId), TimeSpan.FromSeconds(1));
        hub.TryComplete(requestId!, new ProviderResponse { Status = 200, Body = """{"models":[]}""" }, generation);
        Assert.True((await send).IsSuccess);
        Assert.DoesNotContain("sync waiting for companion reconnect", logs);
    }

    [Fact]
    public async Task ProviderReconnectDuringGrace_CompletesCatalog()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(2) };
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var logs = new List<string>();
        var provider = new ChatGptProvider(new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromSeconds(2)));
        var catalog = provider.GetModelCatalogAsync();
        await WaitUntil(() => logs.Contains("sync waiting for companion reconnect"), TimeSpan.FromSeconds(1));
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        await WaitUntil(() => !string.IsNullOrWhiteSpace(requestId), TimeSpan.FromSeconds(1));
        hub.TryComplete(requestId!, new ProviderResponse { Status = 200, Body = """{"models":[{"slug":"gpt-6-pro"}]}""" }, generation);
        var models = await catalog;
        Assert.Contains(models, model => model.Slug == "gpt-6-pro");
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(15);
        }

        Assert.Fail("timed out waiting for condition");
    }
}
