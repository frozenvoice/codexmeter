using System.Text;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ConversationEndpointFallbackTests
{
    [Fact]
    public async Task HungPaginatedHead_FullMapping403_LegacyChunkedMapping_Succeeds()
    {
        var body = LargeProjectedConversationFactory.CreateAtLeastUtf8Bytes("conv-a", CompanionBridgeProtocol.MaxNativeMessageBytes + 2048);
        var node = LargeProjectedConversationFactory.Parse(body);
        Assert.True(ConversationDetailLoader.IsCompleteMapping(node));

        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(8) };
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        hub.Outgoing += message =>
        {
            if (!Enum.TryParse<CompanionOperation>(message.Operation, out var operation) || message.RequestId is null)
            {
                return;
            }

            if (operation == CompanionOperation.GetConversationHead)
            {
                return;
            }

            if (operation == CompanionOperation.GetConversationFull)
            {
                hub.TryCompleteInvokeResult(
                    message.RequestId,
                    CompanionBridgeProtocol.Parse(
                        $$"""{"type":"invokeResult","requestId":"{{message.RequestId}}","operation":"GetConversationFull","status":403,"error":"{{CompanionDiagnostics.Forbidden403}}","body":""}"""),
                    generation);
                return;
            }

            if (operation != CompanionOperation.GetConversationLegacy)
            {
                return;
            }

            foreach (var frame in CompanionChunkProtocol.BuildFrames(message.RequestId, operation, 200, body))
            {
                hub.TryHandleChunkedResult(CompanionBridgeProtocol.Parse(CompanionBridgeProtocol.Serialize(frame)), generation);
            }
        };

        var provider = new ChatGptProvider(new BrowserCompanionTransport(hub))
        {
            ConversationEndpointTimeout = TimeSpan.FromMilliseconds(250)
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var load = await provider.GetConversationMessagesAsync("conv-a", cts.Token);
        Assert.Contains(load.Diagnostics, line => line.Contains("paginated-head timed out", StringComparison.Ordinal));
        Assert.Contains(load.Diagnostics, line => line.Contains("full-mapping unavailable status=403", StringComparison.Ordinal));
        Assert.True(load.Complete, string.Join("; ", load.Diagnostics));
        Assert.False(load.SchemaMismatch);
        var parsed = new ConversationParser(new ModelNormalizer()).Parse(
            load.Conversation,
            new ConversationParseContext { ConversationId = "conv-a" });
        Assert.False(parsed.SchemaMismatch);
        Assert.True(parsed.Events.Count >= 1);
    }

    [Fact]
    public async Task LargePaginatedHead_ChunksAndParsesWithoutFakeSchemaMismatch()
    {
        var body = LargeProjectedConversationFactory.CreateAtLeastUtf8Bytes(
            "conv-bc",
            CompanionBridgeProtocol.MaxNativeMessageBytes + 2048);
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(8) };
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        hub.Outgoing += message =>
        {
            if (!Enum.TryParse<CompanionOperation>(message.Operation, out var operation) || message.RequestId is null)
            {
                return;
            }

            if (operation != CompanionOperation.GetConversationHead)
            {
                return;
            }

            foreach (var frame in CompanionChunkProtocol.BuildFrames(message.RequestId, operation, 200, body))
            {
                Assert.True(CompanionChunkProtocol.FrameFitsNativeLimit(frame));
                hub.TryHandleChunkedResult(CompanionBridgeProtocol.Parse(CompanionBridgeProtocol.Serialize(frame)), generation);
            }
        };

        var provider = new ChatGptProvider(new BrowserCompanionTransport(hub));
        var load = await provider.GetConversationMessagesAsync("conv-bc");
        Assert.DoesNotContain(load.Diagnostics, line => line.Contains("PayloadTooLarge", StringComparison.OrdinalIgnoreCase));
        Assert.True(load.Complete, string.Join("; ", load.Diagnostics));
        Assert.False(load.SchemaMismatch);
        var parsed = new ConversationParser(new ModelNormalizer()).Parse(
            load.Conversation,
            new ConversationParseContext { ConversationId = "conv-bc" });
        Assert.False(parsed.SchemaMismatch);
        Assert.True(parsed.Events.Count >= 1);
    }

    [Fact]
    public async Task FullMappingForbidden_DoesNotAbortWhenLegacyWorks()
    {
        var logs = new List<string>();
        var loader = new ConversationDetailLoader(log: logs.Add)
        {
            ConversationEndpointTimeout = TimeSpan.FromSeconds(2)
        };
        var load = await loader.LoadAsync(
            new DataExportTransport(),
            "conv-full-403",
            (method, path, _, _) =>
            {
                _ = method;
                if (path.Contains("include_full_conversation", StringComparison.Ordinal))
                {
                    throw new ChatGptProviderException(CompanionDiagnostics.Forbidden403, 403);
                }

                if (path.Contains("num_turns", StringComparison.Ordinal))
                {
                    return Task.FromResult<JsonNode?>(new JsonObject { ["conversation_id"] = "conv-full-403" });
                }

                return Task.FromResult<JsonNode?>(ConversationFixtures.NormalPro("conv-full-403"));
            });
        Assert.True(load.Complete);
        Assert.Contains(load.Diagnostics, line => line.Contains("full-mapping unavailable status=403", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("kind=FullMapping", StringComparison.Ordinal) && line.Contains("http403-unsupported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AccountOrIndexForbidden_RemainsFatal()
    {
        var ex = new ChatGptProviderException(CompanionDiagnostics.Forbidden403, 403);
        Assert.True(ex.IsForbidden);
        Assert.True(ex.IsFatalTransportFailure);
        var loader = new ConversationDetailLoader();
        await Assert.ThrowsAsync<ChatGptProviderException>(() => loader.LoadAsync(
            new DataExportTransport(),
            "conv-paginated-403",
            (_, path, _, _) =>
            {
                if (path.Contains("num_turns", StringComparison.Ordinal))
                {
                    throw new ChatGptProviderException(CompanionDiagnostics.Forbidden403, 403);
                }

                throw new InvalidOperationException("fallback should not run after fatal paginated 403");
            }));
    }

    [Fact]
    public void PaginatedProjection_OmitsRedundantMapping()
    {
        var mapping = ConversationFixtures.NormalPro("conv-slim");
        mapping["messages"] = new JsonArray
        {
            ConversationFixtures.DirectPaginatedMessage("user-1", "user", null, 1_777_500_901),
            ConversationFixtures.DirectPaginatedMessage(
                "asst-1",
                "assistant",
                "user-1",
                1_777_500_902,
                requestId: "req-slim",
                modelSlug: "gpt-5-6-pro")
        };
        var head = BridgeProjection.Project(CompanionOperation.GetConversationHead, mapping);
        var older = BridgeProjection.Project(CompanionOperation.GetOlderConversationMessages, mapping);
        var legacy = BridgeProjection.Project(CompanionOperation.GetConversationLegacy, mapping);
        Assert.Null(head?["mapping"]);
        Assert.Null(older?["mapping"]);
        Assert.NotNull(head?["messages"]);
        Assert.NotNull(legacy?["mapping"]);
        var headBytes = Encoding.UTF8.GetByteCount(head!.ToJsonString());
        var legacyBytes = Encoding.UTF8.GetByteCount(legacy!.ToJsonString());
        Assert.True(legacyBytes > headBytes);
    }

    [Fact]
    public async Task AllEndpointsPayloadTooLarge_IsNotSchemaMismatch()
    {
        var loader = new ConversationDetailLoader { ConversationEndpointTimeout = TimeSpan.FromSeconds(2) };
        var calls = new List<string>();
        var load = await loader.LoadAsync(
            new DataExportTransport(),
            "conv-size",
            (_, path, _, _) =>
            {
                calls.Add(path);
                throw new ChatGptProviderException("PayloadTooLarge");
            });
        Assert.False(load.SchemaMismatch);
        Assert.Equal("PayloadTooLarge", SyncFailureClassifier.ClassifyLoad(load));
        Assert.Contains(load.Diagnostics, line => line.Contains("payload too large", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public void OuterBodyTimeout_AllowsEndpointTimeoutPlusLegacy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "t.db"));
        var engine = new SyncEngine(store, new ConversationParser(new ModelNormalizer()), new ModelNormalizer(), new AppLog(dir));
        Assert.Equal(TimeSpan.FromSeconds(45), engine.ConversationBodyTimeout);
        Assert.Equal(TimeSpan.FromSeconds(18), new ConversationDetailLoader().ConversationEndpointTimeout);
        Assert.True(TimeSpan.FromSeconds(18) + TimeSpan.FromSeconds(12) < TimeSpan.FromSeconds(45));
    }
}
