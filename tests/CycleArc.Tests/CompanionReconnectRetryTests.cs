using CycleArc.Providers.ChatGpt;

namespace CycleArc.Tests;

public class CompanionReconnectRetryTests
{
    [Fact]
    public void EveryApprovedOperation_HasExplicitReconnectRetryDecision()
    {
        foreach (var operation in Enum.GetValues<CompanionOperation>())
        {
            var expected = operation switch
            {
                CompanionOperation.GetSessionStatus => true,
                CompanionOperation.GetAccountCheck => true,
                CompanionOperation.GetAccountMe => true,
                CompanionOperation.GetModels => true,
                CompanionOperation.GetConversationIndex => true,
                CompanionOperation.GetArchivedConversationIndex => true,
                CompanionOperation.GetProjects => true,
                CompanionOperation.GetProjectConversations => true,
                CompanionOperation.GetConversationHead => true,
                CompanionOperation.GetConversationFull => true,
                CompanionOperation.GetConversationLegacy => true,
                CompanionOperation.GetOlderConversationMessages => true,
                CompanionOperation.GetQuotaInit => true,
                _ => throw new InvalidOperationException($"unmapped operation {operation}")
            };
            Assert.Equal(expected, CompanionOperationPolicy.IsReconnectRetrySafe(operation));
        }

        Assert.False(CompanionOperationPolicy.IsReconnectRetrySafe((CompanionOperation)int.MaxValue));
        Assert.False(CompanionOperationPolicy.IsReconnectRetrySafe((CompanionOperation)(-1)));
    }

    [Fact]
    public async Task MidRequestDisconnect_QuickReconnect_RetriesOnceAndSucceeds()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var ids = new List<string>();
        var generations = new List<int>();
        hub.Outgoing += message =>
        {
            ids.Add(message.RequestId!);
            generations.Add(hub.ConnectionGeneration);
        };
        var logs = new List<string>();
        var generation = hub.BeginConnection();
        Assert.True(hub.TryAcceptHello(generation, true));
        var transport = new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromSeconds(2));
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        await WaitUntil(() => ids.Count == 1, TimeSpan.FromSeconds(1));
        hub.Disconnect(generation);
        await Task.Delay(80);
        var next = hub.BeginConnection();
        Assert.True(hub.TryAcceptHello(next, true));
        await WaitUntil(() => ids.Count == 2, TimeSpan.FromSeconds(1));
        Assert.NotEqual(ids[0], ids[1]);
        Assert.NotEqual(generation, next);
        Assert.True(hub.TryComplete(
            ids[1],
            new ProviderResponse { Status = 200, Body = """{"models":[]}""" },
            next));
        var response = await send;
        Assert.True(response.IsSuccess);
        Assert.False(response.IsCompanionDisconnected);
        Assert.Equal(2, ids.Count);
        Assert.Contains("companion request interrupted operation=GetModels waiting-reconnect", logs);
        Assert.Contains("companion reconnected retrying operation=GetModels", logs);
        Assert.DoesNotContain("accountId", string.Join(" ", logs), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", string.Join(" ", logs), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MidRequestDisconnect_NoReconnect_ReturnsCompanionDisconnected()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var ids = new List<string>();
        hub.Outgoing += message => ids.Add(message.RequestId!);
        var logs = new List<string>();
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var transport = new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromMilliseconds(200));
        var send = transport.SendAsync("GET", ChatGptEndpoints.AccountsCheck, null, CancellationToken.None);
        await WaitUntil(() => ids.Count == 1, TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        hub.Disconnect(generation);
        var response = await send;
        Assert.True(response.IsCompanionDisconnected);
        Assert.Single(ids);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        Assert.Contains("companion request interrupted operation=GetAccountCheck waiting-reconnect", logs);
        Assert.DoesNotContain("companion reconnected retrying", string.Join(" ", logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MidRequestDisconnect_RetryAlsoDisconnects_DoesNotLoop()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var ids = new List<string>();
        hub.Outgoing += message => ids.Add(message.RequestId!);
        var logs = new List<string>();
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var transport = new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromSeconds(2));
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        await WaitUntil(() => ids.Count == 1, TimeSpan.FromSeconds(1));
        hub.Disconnect(generation);
        var next = hub.BeginConnection();
        hub.TryAcceptHello(next, true);
        await WaitUntil(() => ids.Count == 2, TimeSpan.FromSeconds(1));
        hub.Disconnect(next);
        var response = await send;
        Assert.True(response.IsCompanionDisconnected);
        Assert.Equal(2, ids.Count);
        Assert.Equal(1, logs.Count(line => line.Contains("companion reconnected retrying", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CancellationDuringReconnectGrace_DoesNotRetry()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var ids = new List<string>();
        hub.Outgoing += message => ids.Add(message.RequestId!);
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        using var cts = new CancellationTokenSource();
        var transport = new BrowserCompanionTransport(hub, null, TimeSpan.FromSeconds(8));
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, cts.Token);
        await WaitUntil(() => ids.Count == 1, TimeSpan.FromSeconds(1));
        hub.Disconnect(generation);
        await Task.Delay(30);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        await Task.Delay(50);
        Assert.Single(ids);
    }

    [Fact]
    public async Task NonDisconnectFailure_IsNotRetried()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var ids = new List<string>();
        hub.Outgoing += message => ids.Add(message.RequestId!);
        var logs = new List<string>();
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var transport = new BrowserCompanionTransport(hub, logs.Add, TimeSpan.FromSeconds(2));
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        await WaitUntil(() => ids.Count == 1, TimeSpan.FromSeconds(1));
        Assert.True(hub.TryComplete(ids[0], CompanionBridgeProtocol.TimeoutResponse(), generation));
        var response = await send;
        Assert.True(response.IsBridgeTimeout);
        Assert.False(response.IsCompanionDisconnected);
        Assert.Single(ids);
        Assert.DoesNotContain("waiting-reconnect", string.Join(" ", logs), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Http401_IsNotRetried()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var ids = new List<string>();
        hub.Outgoing += message => ids.Add(message.RequestId!);
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var transport = new BrowserCompanionTransport(hub, null, TimeSpan.FromSeconds(2));
        var send = transport.SendAsync("GET", ChatGptEndpoints.Models, null, CancellationToken.None);
        await WaitUntil(() => ids.Count == 1, TimeSpan.FromSeconds(1));
        Assert.True(hub.TryComplete(ids[0], new ProviderResponse { Status = 401, Error = "Authentication required." }, generation));
        var response = await send;
        Assert.Equal(401, response.Status);
        Assert.Single(ids);
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
