using System.Text.Json.Nodes;
using System.Threading.Channels;
using ProMeter.Companion;
using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class CompanionOperationCorrelationTests
{
    [Fact]
    public async Task MismatchedResultOperation_IsSchemaMismatch()
    {
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetModels);
        var parsed = CompanionBridgeProtocol.Parse(
            $$"""{"type":"invokeResult","requestId":"{{requestId}}","operation":"GetAccountMe","status":200,"body":"{\"id\":\"u1\"}"}""");
        Assert.True(hub.TryCompleteInvokeResult(requestId, parsed, generation));
        var response = await pending;
        Assert.True(response.SchemaMismatch);
        Assert.False(response.IsSuccess);
        Assert.Contains("operation mismatch", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MatchingRequestAndResult_Succeeds()
    {
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetModels);
        var parsed = CompanionBridgeProtocol.Parse(
            $$"""{"type":"invokeResult","requestId":"{{requestId}}","operation":"GetModels","status":200,"body":"{\"models\":[{\"slug\":\"gpt-6-pro\"}]}"}""");
        Assert.True(hub.TryCompleteInvokeResult(requestId, parsed, generation));
        Assert.True((await pending).IsSuccess);
    }

    [Fact]
    public async Task MissingResultOperation_IsSchemaMismatch()
    {
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetModels);
        var parsed = CompanionBridgeProtocol.Parse(
            $$"""{"type":"invokeResult","requestId":"{{requestId}}","status":200,"body":"{\"models\":[]}"}""");
        Assert.True(hub.TryCompleteInvokeResult(requestId, parsed, generation));
        var response = await pending;
        Assert.True(response.SchemaMismatch);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public async Task StaleGeneration_RemainsIgnored()
    {
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetModels);
        var parsed = CompanionBridgeProtocol.Parse(
            $$"""{"type":"invokeResult","requestId":"{{requestId}}","operation":"GetModels","status":200,"body":"{\"models\":[]}"}""");
        Assert.False(hub.TryCompleteInvokeResult(requestId, parsed, generation - 1));
        Assert.False(pending.IsCompleted);
        Assert.True(hub.TryCompleteInvokeResult(requestId, parsed, generation));
        Assert.True((await pending).IsSuccess);
    }

    private static (CompanionRequestHub Hub, int Generation, string RequestId, Task<ProviderResponse> Pending) Pending(
        CompanionOperation operation)
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var pending = hub.RequestAsync(operation, new CompanionOperationArgs(), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        return (hub, generation, requestId!, pending);
    }
}

public class CompanionProjectionIntegrityTests
{
    [Fact]
    public void UnexpectedUserText_IsRejectedWithoutReprojecting()
    {
        var dirty = new JsonObject
        {
            ["title"] = "SYNTHETIC_TITLE_DO_NOT_STORE",
            ["mapping"] = new JsonObject
            {
                ["n1"] = new JsonObject
                {
                    ["id"] = "n1",
                    ["message"] = new JsonObject
                    {
                        ["id"] = "n1",
                        ["content"] = new JsonObject
                        {
                            ["content_type"] = "text",
                            ["parts"] = new JsonArray("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE")
                        }
                    }
                }
            }
        };

        Assert.False(BridgeProjection.TryValidateProjected(CompanionOperation.GetConversationHead, dirty, out var error));
        Assert.Contains("unexpected field", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("SYNTHETIC_TITLE_DO_NOT_STORE", dirty["title"]?.GetValue<string>());
    }

    [Fact]
    public void UnknownMetadataKey_IsRejected()
    {
        var body = new JsonObject
        {
            ["conversation_id"] = "c1",
            ["mapping"] = new JsonObject
            {
                ["n1"] = new JsonObject
                {
                    ["id"] = "n1",
                    ["message"] = new JsonObject
                    {
                        ["id"] = "n1",
                        ["metadata"] = new JsonObject
                        {
                            ["request_id"] = "req-1",
                            ["search_query"] = "SYNTHETIC_QUERY_DO_NOT_STORE"
                        }
                    }
                }
            }
        };

        Assert.False(BridgeProjection.TryValidateProjected(CompanionOperation.GetConversationHead, body, out var error));
        Assert.Contains("search_query", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectNames_DoNotPassNativeBridge()
    {
        var dirty = new JsonObject
        {
            ["gizmos"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = "proj-1",
                    ["gizmo"] = new JsonObject
                    {
                        ["id"] = "proj-1",
                        ["name"] = "SYNTHETIC_PROJECT_NAME_DO_NOT_LEAVE",
                        ["title"] = "SYNTHETIC_PROJECT_TITLE",
                        ["display"] = new JsonObject { ["name"] = "SYNTHETIC_DISPLAY_NAME" }
                    }
                }
            }
        };

        var projected = BridgeProjection.Project(CompanionOperation.GetProjects, dirty);
        Assert.NotNull(projected);
        Assert.True(BridgeProjection.TryValidateProjected(CompanionOperation.GetProjects, projected, out _));
        var json = projected!.ToJsonString();
        Assert.DoesNotContain("SYNTHETIC_PROJECT_NAME_DO_NOT_LEAVE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_PROJECT_TITLE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_DISPLAY_NAME", json, StringComparison.Ordinal);
        Assert.False(BridgeProjection.TryValidateProjected(CompanionOperation.GetProjects, dirty, out _));
    }

    [Fact]
    public void ErrorStatus_IsPreservedForEmptyNonJsonBodies()
    {
        foreach (var status in new[] { 401, 403, 429, 503 })
        {
            var parsed = CompanionBridgeProtocol.Parse(
                $$"""{"type":"invokeResult","operation":"GetModels","status":{{status}},"body":""}""");
            var response = CompanionBridgeProtocol.ToProviderResponse(parsed, CompanionOperation.GetModels);
            Assert.Equal(status, response.Status);
            Assert.False(response.IsSuccess);
        }
    }

    [Fact]
    public void NativeMessageLimit_IsUtf8Bytes()
    {
        Assert.Equal(1_048_576, CompanionBridgeProtocol.MaxNativeMessageBytes);
        Assert.Equal(16_384, CompanionBridgeProtocol.MaxCommandBytes);
    }

    [Fact]
    public void IdOnlyProject_ParsesWithEmptyDisplayName()
    {
        var parsed = AccountParser.ParseProjects(new JsonObject
        {
            ["gizmos"] = new JsonArray
            {
                new JsonObject { ["id"] = "proj-1", ["gizmo"] = new JsonObject { ["id"] = "proj-1" } }
            }
        });
        Assert.True(parsed.RecognizedShape);
        Assert.False(parsed.SchemaMismatch);
        Assert.Equal("proj-1", parsed.Projects[0].Id);
        Assert.True(string.IsNullOrEmpty(parsed.Projects[0].Name));
    }
}

public class CompanionOutgoingWriteFailureTests
{
    [Fact]
    public async Task FailedEnqueue_CompletesPendingRequestImmediately()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var writes = Channel.CreateUnbounded<string>();
        writes.Writer.TryComplete();
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        hub.Outgoing += message =>
        {
            var json = CompanionBridgeProtocol.Serialize(message);
            if (!CompanionOutgoingQueue.TryWrite(writes.Writer, json))
            {
                hub.TryComplete(message.RequestId!, CompanionBridgeProtocol.WriteFailureResponse(), generation);
            }
        };

        var started = DateTime.UtcNow;
        var response = await hub.RequestAsync(CompanionOperation.GetModels, new CompanionOperationArgs(), CancellationToken.None);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
        Assert.Equal("bridge write failed", response.Error);
        Assert.False(response.SchemaMismatch);
        Assert.True(response.IsOffline);
    }
}
