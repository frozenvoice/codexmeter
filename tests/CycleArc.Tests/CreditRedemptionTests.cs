using CycleArc.Codex;

namespace CycleArc.Tests;

public class CreditRedemptionTests
{
    private static readonly CodexLaunchCommand Command = new("synthetic.exe", "app-server --stdio", "synthetic.exe", false);

    [Theory]
    [InlineData("reset", CreditRedemptionOutcome.Reset)]
    [InlineData("alreadyRedeemed", CreditRedemptionOutcome.AlreadyRedeemed)]
    [InlineData("nothingToReset", CreditRedemptionOutcome.NothingToReset)]
    [InlineData("noCredit", CreditRedemptionOutcome.NoCredit)]
    [InlineData("futureValue", CreditRedemptionOutcome.Unknown)]
    public async Task ExplicitConsume_SendsSelectedCreditAndKey_AndCleansUp(string response, CreditRedemptionOutcome expected)
    {
        var factory = new ScriptedCodexProcessFactory
        {
            Responder = line => JsonNode.Parse(line)?["method"]?.ToString() switch
            {
                "initialize" => ["""{"id":1,"result":{}}"""],
                "account/rateLimitResetCredit/consume" =>
                    [new JsonObject { ["id"] = 4, ["result"] = new JsonObject { ["outcome"] = response } }.ToJsonString()],
                _ => []
            }
        };
        var outcome = await new CodexAppServerClient(factory).ConsumeCreditAsync(Command, "test", "synthetic-credit",
            "synthetic-attempt", CancellationToken.None);
        Assert.Equal(expected, outcome);
        var messages = factory.LastProcess!.Received.Select(x => JsonNode.Parse(x)!).ToList();
        Assert.Equal(new[] { "initialize", "initialized", "account/rateLimitResetCredit/consume" },
            messages.Select(x => x["method"]!.ToString()));
        Assert.Equal("synthetic-credit", messages[^1]["params"]!["creditId"]!.ToString());
        Assert.Equal("synthetic-attempt", messages[^1]["params"]!["idempotencyKey"]!.ToString());
        Assert.True(factory.LastProcess.HasExited || factory.LastProcess.KillCalled);
    }

    [Theory]
    [InlineData("""{"id":4,"result":{}}""")]
    [InlineData("""{"id":4,"result":{"outcome":27}}""")]
    [InlineData("""{"id":4,"error":{"code":-32601,"message":"synthetic"}}""")]
    public async Task MalformedOrErrorResponse_NeverClaimsSuccess(string response)
    {
        var factory = new ScriptedCodexProcessFactory { Responder = line =>
            JsonNode.Parse(line)?["method"]?.ToString() switch
            {
                "initialize" => ["""{"id":1,"result":{}}"""],
                "account/rateLimitResetCredit/consume" => [response],
                _ => []
            }};
        Assert.Equal(CreditRedemptionOutcome.Unknown,
            await new CodexAppServerClient(factory).ConsumeCreditAsync(Command, "test", "credit", "attempt", CancellationToken.None));
        Assert.True(factory.LastProcess!.HasExited || factory.LastProcess.KillCalled);
    }

    [Fact]
    public async Task Service_SerializesRefreshAndUse_AndReusesUncertainAttemptKey()
    {
        var requestedKeys = new List<string>();
        var reached = new TaskCompletionSource();
        using var release = new ManualResetEventSlim();
        var factory = new ScriptedCodexProcessFactory { Responder = line =>
        {
            var node = JsonNode.Parse(line)!;
            if (node["method"]?.ToString() == "account/rateLimits/read")
                return ["""{"id":3,"result":{"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":10080}},"rateLimitResetCredits":{"availableCount":1,"credits":[{"id":"test-credit","status":"available","resetType":"codexRateLimits","expiresAt":1999999999}]}}}"""];
            if (node["method"]?.ToString() == "account/rateLimitResetCredit/consume")
            {
                requestedKeys.Add(node["params"]!["idempotencyKey"]!.ToString());
                reached.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test release");
                return ["""{"id":4,"result":{"outcome":"unrecognized"}}"""];
            }
            return CodexScript.Standard(line);
        }};
        var files = new MemoryCodexFileSystem();
        files.Files.Add(@"C:\Tools\codex.exe");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var service = new CodexQuotaService(new CodexExecutableLocator(files), new CodexAppServerClient(factory),
                new CodexSnapshotStore(path), "test");
            await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);
            Assert.Equal(CodexQuotaStatus.Available, service.Snapshot.Status);
            Assert.Single(service.Snapshot.RedeemableCredits);
            Assert.Empty(requestedKeys); // Read/startup cannot redeem.
            var first = service.ConsumeCreditAsync("test-credit", @"C:\Tools\codex.exe", CancellationToken.None);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(CreditRedemptionOutcome.Busy,
                await service.ConsumeCreditAsync("test-credit", @"C:\Tools\codex.exe", CancellationToken.None));
            Assert.Equal("already-running", (await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None)).FailureCategory);
            release.Set();
            Assert.Equal(CreditRedemptionOutcome.Unknown, await first);
            Assert.Equal(CreditRedemptionOutcome.Unavailable,
                await service.ConsumeCreditAsync("test-credit", @"C:\Tools\codex.exe", CancellationToken.None));
            await service.RefreshAsync(@"C:\Tools\codex.exe", CancellationToken.None);
            await service.ConsumeCreditAsync("test-credit", @"C:\Tools\codex.exe", CancellationToken.None);
            Assert.Equal(2, requestedKeys.Count);
            Assert.Equal(requestedKeys[0], requestedKeys[1]);
            Assert.DoesNotContain("test-credit", File.ReadAllText(path));
        }
        finally { release.Set(); File.Delete(path); }
    }
    [Fact]
    public async Task FailedHandshake_DoesNotSendConsume()
    {
        var factory = new ScriptedCodexProcessFactory { Responder = _ => ["""{"id":1,"error":{"code":-1}}"""] };
        Assert.Equal(CreditRedemptionOutcome.Unavailable,
            await new CodexAppServerClient(factory).ConsumeCreditAsync(Command, "test", "credit", "attempt", CancellationToken.None));
        Assert.Single(factory.LastProcess!.Received);
    }

    [Fact]
    public async Task CancelledAfterSend_IsUncertain_NoAutomaticRetry()
    {
        using var cts = new CancellationTokenSource();
        var factory = new ScriptedCodexProcessFactory { Responder = line =>
        {
            if (JsonNode.Parse(line)?["method"]?.ToString() == "initialize") return ["""{"id":1,"result":{}}"""];
            if (JsonNode.Parse(line)?["method"]?.ToString() == "account/rateLimitResetCredit/consume") cts.Cancel();
            return [];
        }};
        Assert.Equal(CreditRedemptionOutcome.Unknown,
            await new CodexAppServerClient(factory).ConsumeCreditAsync(Command, "test", "credit", "attempt", cts.Token));
        Assert.Single(factory.LastProcess!.Received, x => x.Contains("account/rateLimitResetCredit/consume"));
        Assert.True(factory.LastProcess.HasExited || factory.LastProcess.KillCalled);
    }

    [Fact]
    public void LiveIds_AreNotPersisted_AndDoNotEnableCachedRedemption()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new CodexSnapshotStore(path);
            store.Save(CodexQuotaSnapshot.Empty(CodexQuotaStatus.Available) with
            {
                ResetCreditsAvailable = 1, ResetCreditExpirations = [DateTimeOffset.UtcNow.AddDays(1)],
                RedeemableCredits = [new("synthetic-private-credit-id", DateTimeOffset.UtcNow.AddDays(1))]
            });
            Assert.DoesNotContain("synthetic-private-credit-id", File.ReadAllText(path));
            Assert.Empty(store.Load()!.RedeemableCredits);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DuplicateOrMalformedCreditIdentity_DisablesUse()
    {
        var response = JsonNode.Parse("""{"result":{"rateLimitResetCredits":{"credits":[{"id":"same","status":"available","resetType":"codexRateLimits"},{"id":"same","status":"available","resetType":"codexRateLimits"}]}}}""");
        Assert.Empty(CodexRateLimitParser.ReadRedeemableCredits(response));
    }
}
