using System.Text.Json.Nodes;
using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class ConversationDetailLoaderTests
{
    [Fact]
    public async Task ShortMapping_IsAcceptedWhenCurrentNodeChainIsComplete()
    {
        var shortChat = ConversationFixtures.NormalPro("conv-short");
        var loader = new ConversationDetailLoader();
        var result = await loader.LoadAsync(null!, "conv-short", (method, path, _, _) =>
        {
            Assert.Equal("GET", method);
            if (path.Contains("include_full_conversation=true", StringComparison.Ordinal))
            {
                return Task.FromResult<JsonNode?>(shortChat.DeepClone());
            }

            throw new InvalidOperationException("Unexpected path " + path);
        });

        Assert.True(result.Complete);
        Assert.False(result.SchemaMismatch);
        Assert.True(result.MappingUsed);
        AssertEmptyBodies(result.Conversation);
        var parsed = new ConversationParser(new ModelNormalizer()).Parse(result.Conversation, new ConversationParseContext { ConversationId = "conv-short" });
        Assert.False(parsed.SchemaMismatch);
        Assert.Single(parsed.Events);
    }

    [Fact]
    public async Task LongMapping_IsAcceptedWithoutPagination()
    {
        var longChat = ConversationFixtures.LongConversation("conv-long", turns: 24);
        var loader = new ConversationDetailLoader();
        var result = await loader.LoadAsync(null!, "conv-long", (_, path, _, _) =>
        {
            if (path.Contains("include_full_conversation=true", StringComparison.Ordinal))
            {
                return Task.FromResult<JsonNode?>(longChat.DeepClone());
            }

            throw new InvalidOperationException("Unexpected path " + path);
        });

        Assert.True(result.Complete);
        Assert.True(ConversationDetailLoader.IsCompleteMapping(result.Conversation));
        var parsed = new ConversationParser(new ModelNormalizer()).Parse(result.Conversation, new ConversationParseContext { ConversationId = "conv-long" });
        Assert.Equal(24, parsed.Events.Count);
    }

    [Fact]
    public async Task IncompleteMapping_FallsBackToPaginatedMessages()
    {
        var loader = new ConversationDetailLoader();
        var fetches = new List<string>();
        var result = await loader.LoadAsync(null!, "conv-paged", (_, path, _, _) =>
        {
            fetches.Add(path);
            if (path.Contains("include_full_conversation=true", StringComparison.Ordinal)
                || path.EndsWith("/conversation/conv-paged", StringComparison.Ordinal))
            {
                return Task.FromResult<JsonNode?>(ConversationFixtures.IncompleteMapping("conv-paged"));
            }

            if (path.Contains("/conversations/conv-paged?", StringComparison.Ordinal)
                && path.Contains("num_turns=100", StringComparison.Ordinal))
            {
                return Task.FromResult<JsonNode?>(ConversationFixtures.PaginatedHead());
            }

            if (path.Contains("/messages?", StringComparison.Ordinal) && path.Contains("before=cursor-older", StringComparison.Ordinal))
            {
                return Task.FromResult<JsonNode?>(ConversationFixtures.PaginatedOlder());
            }

            throw new InvalidOperationException("Unexpected path " + path);
        });

        Assert.Contains(fetches, path => path.Contains("/messages?", StringComparison.Ordinal));
        Assert.True(result.Complete);
        Assert.True(result.PaginatedUsed);
        Assert.Equal(2, result.PagesFetched);
        Assert.True(ConversationDetailLoader.IsCompleteMapping(result.Conversation));
        AssertEmptyBodies(result.Conversation);
        var parsed = new ConversationParser(new ModelNormalizer()).Parse(result.Conversation, new ConversationParseContext { ConversationId = "conv-paged" });
        Assert.Equal(2, parsed.Events.Count);
    }

    [Fact]
    public async Task RepeatedCursor_MarksConversationIncomplete()
    {
        var loader = new ConversationDetailLoader();
        var looping = new JsonObject
        {
            ["conversation_id"] = "conv-loop",
            ["messages"] = new JsonArray
            {
                ConversationFixtures.Node("asst-1", "assistant", "user-1", 1_777_500_000, requestId: "req", modelSlug: "gpt-5-6-pro")
            },
            ["page_info"] = new JsonObject
            {
                ["has_previous_page"] = true,
                ["start_cursor"] = "same-cursor"
            }
        };
        var result = await loader.LoadAsync(null!, "conv-loop", (_, path, _, _) =>
        {
            if (path.Contains("include_full_conversation", StringComparison.Ordinal)
                || path.Contains("/conversation/conv-loop", StringComparison.Ordinal))
            {
                throw new ChatGptProviderException("missing", 404);
            }

            return Task.FromResult<JsonNode?>(looping.DeepClone());
        });

        Assert.False(result.Complete);
        Assert.Contains(result.Diagnostics, d => d.Contains("Repeated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CapturedShapeJsonFixtures_ParseShortAndPaginated()
    {
        var fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var shortChat = ChatGptJson.ParseNode(File.ReadAllText(Path.Combine(fixtures, "conversation-short.json")));
        Assert.True(ConversationDetailLoader.IsCompleteMapping(shortChat));
        var parsed = new ConversationParser(new ModelNormalizer()).Parse(shortChat, new ConversationParseContext { ConversationId = "conv-short" });
        Assert.Single(parsed.Events);

        var head = ChatGptJson.ParseNode(File.ReadAllText(Path.Combine(fixtures, "conversation-paginated-head.json")));
        var older = ChatGptJson.ParseNode(File.ReadAllText(Path.Combine(fixtures, "conversation-paginated-older.json")));
        Assert.False(ConversationDetailLoader.IsCompleteMapping(head));
        var reconstructed = ConversationDetailLoader.ReconstructMapping("conv-paged", head!, Collect(head, older));
        Assert.True(ConversationDetailLoader.IsCompleteMapping(reconstructed));
        Assert.True(ConversationDetailLoader.IsCompleteMapping(ConversationFixtures.LongConversation("conv-long", turns: 24)));
    }

    private static Dictionary<string, JsonObject> Collect(params JsonNode?[] pages)
    {
        var messages = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            if (page is null)
            {
                continue;
            }

            foreach (var item in ChatGptJson.Enumerate(page["messages"]))
            {
                if (item is JsonObject obj)
                {
                    var id = ChatGptJson.GetString(obj, "id") ?? ChatGptJson.GetString(obj["message"], "id");
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        messages[id] = obj;
                    }
                }
            }
        }

        return messages;
    }

    [Fact]
    public void FromFixture_DoesNotTreatIncompleteMappingAsComplete()
    {
        var result = ConversationDetailLoader.FromFixture(ConversationFixtures.IncompleteMapping());
        Assert.False(result.Complete);
    }

    private static void AssertEmptyBodies(JsonNode? node)
    {
        Assert.NotNull(node);
        Walk(node, current =>
        {
            if (current is JsonObject obj && obj["parts"] is JsonArray parts)
            {
                Assert.Empty(parts);
            }

            if (current is JsonObject text && text.ContainsKey("text") && text["content"] is JsonObject)
            {
                Assert.True(text["text"] is null);
            }
        });
    }

    private static void Walk(JsonNode? node, Action<JsonNode> visit)
    {
        if (node is null)
        {
            return;
        }

        visit(node);
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                Walk(property.Value, visit);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                Walk(item, visit);
            }
        }
    }
}
