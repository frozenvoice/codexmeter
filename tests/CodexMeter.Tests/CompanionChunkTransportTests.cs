using System.Text;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class CompanionChunkTransportTests
{
    [Fact]
    public void SmallProjectedBody_StaysOnSingleInvokeResult()
    {
        var body = ConversationFixtures.NormalPro().ToJsonString();
        var invoke = CompanionBridgeProtocol.Serialize(new CompanionBridgeMessage
        {
            Type = CompanionBridgeProtocol.InvokeResult,
            RequestId = "req-small",
            Operation = nameof(CompanionOperation.GetConversationHead),
            Status = 200,
            Body = body
        });
        Assert.True(Encoding.UTF8.GetByteCount(invoke) < CompanionBridgeProtocol.MaxNativeMessageBytes);
        Assert.True(Encoding.UTF8.GetByteCount(invoke) < CompanionChunkProtocol.MaxChunkFrameBytes);
    }

    [Fact]
    public async Task LargeProjectedBody_ChunksStayUnderNativeLimitAndReassemble()
    {
        var body = LargeProjectedConversationFactory.CreateAtLeastUtf8Bytes("conv-large", CompanionBridgeProtocol.MaxNativeMessageBytes + 4096);
        Assert.True(Encoding.UTF8.GetByteCount(body) > CompanionBridgeProtocol.MaxNativeMessageBytes);
        Assert.True(Encoding.UTF8.GetByteCount(body) < CompanionChunkProtocol.MaxAssembledProjectedBytes);
        Assert.DoesNotContain("parts", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_", body, StringComparison.Ordinal);

        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        foreach (var frame in CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationLegacy, 200, body))
        {
            Assert.True(CompanionChunkProtocol.FrameFitsNativeLimit(frame), $"frame {frame.Type} too large");
            Assert.True(hub.TryHandleChunkedResult(ParseFrame(frame), generation));
        }

        var response = await pending;
        Assert.True(response.IsSuccess, response.Error);
        Assert.False(response.SchemaMismatch);
        Assert.False(response.IsPayloadTooLarge);
        Assert.Equal(body, response.Body);
        var node = JsonNode.Parse(response.Body);
        Assert.True(BridgeProjection.TryValidateProjected(CompanionOperation.GetConversationLegacy, node, out var error), error);
    }

    [Fact]
    public async Task OutOfOrderChunks_Reassemble()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-ooo", 1200);
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationHead);
        var frames = CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationHead, 200, body).ToList();
        var start = frames[0];
        var end = frames[^1];
        var chunks = frames.Skip(1).Take(frames.Count - 2).Reverse().ToList();
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(start), generation));
        foreach (var chunk in chunks)
        {
            Assert.True(hub.TryHandleChunkedResult(ParseFrame(chunk), generation));
        }

        Assert.True(hub.TryHandleChunkedResult(ParseFrame(end), generation));
        Assert.Equal(body, (await pending).Body);
    }

    [Fact]
    public async Task DuplicateIdenticalChunk_IsIgnored()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-dup", 1200);
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        var frames = CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationLegacy, 200, body).ToList();
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[1]), generation));
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[1]), generation));
        for (var i = 2; i < frames.Count; i++)
        {
            Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[i]), generation));
        }

        Assert.Equal(body, (await pending).Body);
    }

    [Fact]
    public async Task MissingChunk_FailsProtocolNotSchemaMismatch()
    {
        var body = LargeProjectedConversationFactory.CreateAtLeastUtf8Bytes("conv-miss", CompanionChunkProtocol.ChunkRawBytes + 8192);
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationFull);
        var frames = CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationFull, 200, body).ToList();
        Assert.True(frames.Count >= 4);
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[1]), generation));
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[^1]), generation));
        var response = await pending;
        Assert.False(response.IsSuccess);
        Assert.False(response.SchemaMismatch);
        Assert.Equal(CompanionChunkProtocol.ProtocolError, response.Error);
    }

    [Fact]
    public async Task InvalidChunkIndex_FailsProtocol()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-idx", 800);
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        var frames = CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationLegacy, 200, body).ToList();
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        var bad = frames[1];
        bad.ChunkIndex = 99;
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(bad), generation));
        var response = await pending;
        Assert.Equal(CompanionChunkProtocol.ProtocolError, response.Error);
        Assert.False(response.SchemaMismatch);
    }

    [Fact]
    public async Task WrongRequestId_IsIgnored()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-id", 400);
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        var frames = CompanionChunkProtocol.BuildFrames("other-request", CompanionOperation.GetConversationLegacy, 200, body).ToList();
        Assert.False(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        Assert.False(pending.IsCompleted);
        foreach (var frame in CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationLegacy, 200, body))
        {
            Assert.True(hub.TryHandleChunkedResult(ParseFrame(frame), generation));
        }

        Assert.True((await pending).IsSuccess);
    }

    [Fact]
    public async Task WrongOperation_IsOperationMismatch()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-op", 400);
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        var frames = CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationHead, 200, body).ToList();
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        var response = await pending;
        Assert.True(response.SchemaMismatch);
        Assert.Contains("operation mismatch", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChunkCountTooLarge_IsPayloadTooLarge()
    {
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        var start = new CompanionBridgeMessage
        {
            Type = CompanionChunkProtocol.InvokeResultStart,
            RequestId = requestId,
            Operation = nameof(CompanionOperation.GetConversationLegacy),
            Chunked = true,
            ChunkCount = CompanionChunkProtocol.MaxChunkCount + 1,
            TotalProjectedBytes = 1024,
            Status = 200
        };
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(start), generation));
        var response = await pending;
        Assert.True(response.IsPayloadTooLarge);
        Assert.False(response.SchemaMismatch);
        Assert.Equal("PayloadTooLarge", SyncFailureClassifier.Classify(new ChatGptProviderException("PayloadTooLarge")).Category);
    }

    [Fact]
    public async Task TotalAssembledCap_IsPayloadTooLargeNotSchemaMismatch()
    {
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        var start = new CompanionBridgeMessage
        {
            Type = CompanionChunkProtocol.InvokeResultStart,
            RequestId = requestId,
            Operation = nameof(CompanionOperation.GetConversationLegacy),
            Chunked = true,
            ChunkCount = 1,
            TotalProjectedBytes = CompanionChunkProtocol.MaxAssembledProjectedBytes + 1,
            Status = 200
        };
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(start), generation));
        var response = await pending;
        Assert.True(response.IsPayloadTooLarge);
        Assert.False(response.SchemaMismatch);
        Assert.False(response.IsOffline);
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("응답 크기 초과", DisplayFormatting.FailureCategoryLabel("PayloadTooLarge"));
            Assert.DoesNotContain("응답 형식 불일치", DisplayFormatting.FailureCategoryLabel("PayloadTooLarge"), StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public async Task DisconnectMidTransfer_ClearsAssembly()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-dc", 1200);
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        var frames = CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationLegacy, 200, body).ToList();
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[1]), generation));
        hub.Disconnect(generation);
        var response = await pending;
        Assert.True(response.IsCompanionDisconnected);
        var nextGeneration = hub.BeginConnection();
        hub.TryAcceptHello(nextGeneration, true);
        Assert.False(hub.TryHandleChunkedResult(ParseFrame(frames[^1]), nextGeneration));
    }

    [Fact]
    public async Task CancellationMidTransfer_ClearsAssembly()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-cancel", 1200);
        var hub = ConnectedHub();
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var generation = hub.ConnectionGeneration;
        using var cts = new CancellationTokenSource();
        var pending = hub.RequestAsync(CompanionOperation.GetConversationLegacy, new CompanionOperationArgs { ConversationId = "conv-cancel" }, cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        var frames = CompanionChunkProtocol.BuildFrames(requestId!, CompanionOperation.GetConversationLegacy, 200, body).ToList();
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(hub.TryHandleChunkedResult(ParseFrame(frames[1]), generation));
    }

    [Fact]
    public async Task TimeoutMidTransfer_ClearsAssembly()
    {
        var body = LargeProjectedConversationFactory.CreateCompleteMappingJson("conv-to", 1200);
        var hub = ConnectedHub(TimeSpan.FromMilliseconds(80));
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var generation = hub.ConnectionGeneration;
        var pending = hub.RequestAsync(CompanionOperation.GetConversationLegacy, new CompanionOperationArgs { ConversationId = "conv-to" }, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        var frames = CompanionChunkProtocol.BuildFrames(requestId!, CompanionOperation.GetConversationLegacy, 200, body).ToList();
        Assert.True(hub.TryHandleChunkedResult(ParseFrame(frames[0]), generation));
        var response = await pending;
        Assert.True(response.IsBridgeTimeout);
        Assert.False(hub.TryHandleChunkedResult(ParseFrame(frames[1]), generation));
    }

    [Fact]
    public async Task TokenLikeText_IsRejectedAfterReassembly()
    {
        var leaked = """{"conversation_id":"c","current_node":"n1","mapping":{"n1":{"id":"n1","message":{"id":"n1","author":{"role":"assistant"},"metadata":{"request_id":"r1","model_slug":"gpt-5-6-pro"},"content":{"content_type":"text"}}}},"accessToken":"must-not-cross"}""";
        var (hub, generation, requestId, pending) = Pending(CompanionOperation.GetConversationLegacy);
        foreach (var frame in CompanionChunkProtocol.BuildFrames(requestId, CompanionOperation.GetConversationLegacy, 200, leaked))
        {
            hub.TryHandleChunkedResult(ParseFrame(frame), generation);
        }

        var response = await pending;
        Assert.True(response.SchemaMismatch);
        Assert.False(response.IsPayloadTooLarge);
    }

    [Fact]
    public void PromptContent_IsRejectedByProjectionBeforeChunking()
    {
        var raw = ConversationFixtures.NormalPro();
        raw["mapping"]!["asst-1"]!["message"]!["content"] = new JsonObject
        {
            ["content_type"] = "text",
            ["parts"] = new JsonArray("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE")
        };
        var projected = BridgeProjection.Project(CompanionOperation.GetConversationLegacy, raw);
        Assert.NotNull(projected);
        Assert.False(BridgeProjection.ContainsPromptOrResponseText(projected));
        Assert.DoesNotContain("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE", projected!.ToJsonString(), StringComparison.Ordinal);
        Assert.True(BridgeProjection.TryValidateProjected(CompanionOperation.GetConversationLegacy, projected, out var error), error);
    }

    [Fact]
    public void PayloadTooLarge_IsNotOfflineOrSchemaMismatch()
    {
        var parsed = CompanionBridgeProtocol.Parse(
            """{"type":"invokeResult","requestId":"r","operation":"GetConversationLegacy","payloadTooLarge":true,"error":"PayloadTooLarge","schemaMismatch":false}""");
        var response = CompanionBridgeProtocol.ToProviderResponse(parsed, CompanionOperation.GetConversationLegacy);
        Assert.True(response.IsPayloadTooLarge);
        Assert.False(response.SchemaMismatch);
        Assert.False(response.IsOffline);
        Assert.False(response.IsNetworkUnavailable);
        var ex = new ChatGptProviderException("PayloadTooLarge");
        Assert.True(ex.IsPayloadTooLarge);
        Assert.False(ex.SchemaMismatch);
        Assert.False(ex.IsFatalTransportFailure);
        Assert.False(ex.IsOffline);
        Assert.Equal("PayloadTooLarge", SyncFailureClassifier.Classify(ex).Category);
    }

    private static CompanionParseResult ParseFrame(CompanionBridgeMessage frame)
    {
        var parsed = CompanionBridgeProtocol.Parse(CompanionBridgeProtocol.Serialize(frame));
        Assert.True(parsed.Accepted, parsed.Error);
        return parsed;
    }

    private static (CompanionRequestHub Hub, int Generation, string RequestId, Task<ProviderResponse> Pending) Pending(
        CompanionOperation operation)
    {
        var hub = ConnectedHub();
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var pending = hub.RequestAsync(operation, new CompanionOperationArgs { ConversationId = "conv-chunk" }, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        return (hub, hub.ConnectionGeneration, requestId!, pending);
    }

    private static CompanionRequestHub ConnectedHub(TimeSpan? timeout = null)
    {
        var hub = new CompanionRequestHub { RequestTimeout = timeout ?? TimeSpan.FromSeconds(8) };
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        return hub;
    }

}
