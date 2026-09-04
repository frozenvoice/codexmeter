namespace ProMeter.Providers.ChatGpt;

public enum ConversationEndpointKind
{
    PaginatedTurns,
    PaginatedOlder,
    FullMapping,
    LegacyMapping
}

public sealed class EndpointCapabilityCache
{
    private readonly HashSet<ConversationEndpointKind> _unsupported = [];

    public bool IsSupported(ConversationEndpointKind kind) => !_unsupported.Contains(kind);

    public void MarkUnsupported(ConversationEndpointKind kind) => _unsupported.Add(kind);

    public IReadOnlyCollection<ConversationEndpointKind> Unsupported => _unsupported;
}

public sealed class ConversationDetailLoader
{
    public const int NumTurns = 100;
    public const int MaxPages = 40;

    public EndpointCapabilityCache Capabilities { get; }

    public ConversationDetailLoader(EndpointCapabilityCache? capabilities = null)
    {
        Capabilities = capabilities ?? new EndpointCapabilityCache();
    }

    public async Task<ConversationLoadResult> LoadAsync(
        IChatGptTransport transport,
        string conversationId,
        Func<string, string, string?, CancellationToken, Task<JsonNode?>> fetch,
        CancellationToken cancellationToken = default)
    {
        _ = transport;
        var diagnostics = new List<string>();
        JsonNode? seed = null;

        if (Capabilities.IsSupported(ConversationEndpointKind.PaginatedTurns))
        {
            var turns = await TryGet(fetch, "GET", ChatGptEndpoints.ConversationTurns(conversationId), cancellationToken, diagnostics, "paginated-head", ConversationEndpointKind.PaginatedTurns);
            if (IsCompleteMapping(turns))
            {
                return Complete(turns, mapping: true, diagnostics);
            }

            if (turns is not null && (HasMessages(turns) || HasPageInfo(turns)))
            {
                return await LoadPaginatedAsync(conversationId, fetch, turns, diagnostics, cancellationToken);
            }

            if (turns is not null)
            {
                diagnostics.Add("Paginated head was incomplete.");
                seed = turns;
            }
        }

        if (Capabilities.IsSupported(ConversationEndpointKind.FullMapping))
        {
            var full = await TryGet(fetch, "GET", ChatGptEndpoints.ConversationFull(conversationId), cancellationToken, diagnostics, "full-mapping", ConversationEndpointKind.FullMapping);
            if (IsCompleteMapping(full))
            {
                return Complete(full, mapping: true, diagnostics);
            }

            if (full is not null)
            {
                diagnostics.Add("Full mapping response was incomplete.");
                seed ??= full;
            }
        }

        if (Capabilities.IsSupported(ConversationEndpointKind.LegacyMapping))
        {
            var legacy = await TryGet(fetch, "GET", ChatGptEndpoints.ConversationById(conversationId), cancellationToken, diagnostics, "legacy-mapping", ConversationEndpointKind.LegacyMapping);
            if (IsCompleteMapping(legacy))
            {
                return Complete(legacy, mapping: true, diagnostics);
            }

            if (legacy is not null)
            {
                diagnostics.Add("Legacy mapping response was incomplete.");
                seed ??= legacy;
            }
        }

        return await LoadPaginatedAsync(conversationId, fetch, seed, diagnostics, cancellationToken);
    }

    public async Task<ConversationLoadResult> LoadPaginatedAsync(
        string conversationId,
        Func<string, string, string?, CancellationToken, Task<JsonNode?>> fetch,
        JsonNode? seed,
        List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        JsonNode? first = seed is not null && (HasMessages(seed) || HasPageInfo(seed))
            ? seed
            : null;
        if (first is null && Capabilities.IsSupported(ConversationEndpointKind.PaginatedTurns))
        {
            first = await TryGet(fetch, "GET", ChatGptEndpoints.ConversationTurns(conversationId), cancellationToken, diagnostics, "paginated-head", ConversationEndpointKind.PaginatedTurns);
        }

        if (first is null)
        {
            diagnostics.Add("Paginated conversation head was empty.");
            return new ConversationLoadResult
            {
                Conversation = seed,
                Complete = false,
                SchemaMismatch = true,
                Diagnostics = diagnostics
            };
        }

        var messages = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        CollectMessages(first, messages);
        StripBodies(first);
        var pages = 1;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var pageInfo = ReadPageInfo(first);
        var complete = true;

        while (pageInfo.HasPreviousPage == true && pages < MaxPages)
        {
            var cursor = pageInfo.StartCursor;
            if (string.IsNullOrWhiteSpace(cursor) || !seenCursors.Add(cursor))
            {
                diagnostics.Add("Repeated or missing start_cursor while has_previous_page is true.");
                complete = false;
                break;
            }

            if (!Capabilities.IsSupported(ConversationEndpointKind.PaginatedOlder))
            {
                diagnostics.Add("Older messages endpoint is unsupported.");
                complete = false;
                break;
            }

            var older = await TryGet(
                fetch,
                "GET",
                ChatGptEndpoints.ConversationOlderMessages(conversationId, cursor),
                cancellationToken,
                diagnostics,
                "paginated-older",
                ConversationEndpointKind.PaginatedOlder);
            if (older is null)
            {
                diagnostics.Add("Older page fetch failed.");
                complete = false;
                break;
            }

            CollectMessages(older, messages);
            pages++;
            pageInfo = ReadPageInfo(older);
        }

        if (pageInfo.HasPreviousPage == true && pages >= MaxPages)
        {
            diagnostics.Add("Safety page cap reached before conversation was complete.");
            complete = false;
        }

        if (messages.Count == 0)
        {
            diagnostics.Add("No messages could be reconstructed.");
            return new ConversationLoadResult
            {
                Conversation = first,
                Complete = false,
                SchemaMismatch = true,
                PaginatedUsed = true,
                PagesFetched = pages,
                Diagnostics = diagnostics
            };
        }

        var reconstructed = ReconstructMapping(conversationId, first, messages);
        if (!IsCompleteMapping(reconstructed))
        {
            diagnostics.Add("Reconstructed mapping is incomplete.");
            complete = false;
        }

        return new ConversationLoadResult
        {
            Conversation = reconstructed,
            Complete = complete,
            SchemaMismatch = !complete && messages.Count == 0,
            PaginatedUsed = true,
            PagesFetched = pages,
            Diagnostics = diagnostics
        };
    }

    public static bool IsCompleteMapping(JsonNode? node)
    {
        if (node is null)
        {
            return false;
        }

        if (node["mapping"] is not JsonObject mapping || mapping.Count == 0)
        {
            return false;
        }

        var current = ChatGptJson.GetString(node, "current_node", "currentNode");
        if (string.IsNullOrWhiteSpace(current) || !mapping.ContainsKey(current))
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = current;
        while (!string.IsNullOrWhiteSpace(cursor) && cursor is not "none" and not "null")
        {
            if (!seen.Add(cursor))
            {
                return false;
            }

            if (!mapping.TryGetPropertyValue(cursor, out var item) || item is not JsonObject obj)
            {
                return false;
            }

            cursor = ChatGptJson.GetString(obj, "parent");
        }

        return true;
    }

    public static JsonObject ReconstructMapping(string conversationId, JsonNode seed, IReadOnlyDictionary<string, JsonObject> messages)
    {
        var mapping = new JsonObject();
        foreach (var (id, node) in messages)
        {
            mapping[id] = node.DeepClone();
        }

        foreach (var property in mapping.ToList())
        {
            if (property.Value is not JsonObject node)
            {
                continue;
            }

            var parent = ChatGptJson.GetString(node, "parent");
            if (string.IsNullOrWhiteSpace(parent) || mapping[parent] is not JsonObject parentNode)
            {
                continue;
            }

            if (parentNode["children"] is not JsonArray children)
            {
                children = [];
                parentNode["children"] = children;
            }

            if (children.All(item => item?.GetValue<string>() != property.Key))
            {
                children.Add(property.Key);
            }
        }

        var current = ChatGptJson.GetString(seed, "current_node", "currentNode")
                      ?? messages.Values
                          .OrderByDescending(node => TimestampParser.ToDateTimeOffset(node["message"] ?? node, "create_time", "createTime", "update_time"))
                          .Select(node => ChatGptJson.GetString(node, "id") ?? ChatGptJson.GetString(node["message"], "id"))
                          .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

        var result = new JsonObject
        {
            ["conversation_id"] = ChatGptJson.GetString(seed, "conversation_id", "id") ?? conversationId,
            ["current_node"] = current,
            ["mapping"] = mapping
        };
        var update = TimestampParser.ToUnixSeconds(seed, "update_time", "updateTime");
        if (update > 0)
        {
            result["update_time"] = update;
        }

        return result;
    }

    public static void StripBodies(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["content"] is JsonObject content)
            {
                if (content["parts"] is JsonArray)
                {
                    content["parts"] = new JsonArray();
                }

                content.Remove("text");
            }

            foreach (var property in obj.ToList())
            {
                StripBodies(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                StripBodies(item);
            }
        }
    }

    public static ConversationLoadResult FromFixture(JsonNode? body)
    {
        if (body is null)
        {
            return new ConversationLoadResult { Complete = false, SchemaMismatch = true, Diagnostics = ["Fixture body was null."] };
        }

        StripBodies(body);
        var complete = IsCompleteMapping(body);
        return new ConversationLoadResult
        {
            Conversation = body,
            Complete = complete,
            SchemaMismatch = !complete && body["mapping"] is null && !HasMessages(body),
            MappingUsed = body["mapping"] is JsonObject,
            Diagnostics = complete ? [] : ["Fixture mapping was incomplete."]
        };
    }

    private static ConversationLoadResult Complete(JsonNode? node, bool mapping, List<string> diagnostics)
    {
        StripBodies(node);
        return new ConversationLoadResult
        {
            Conversation = node,
            Complete = true,
            MappingUsed = mapping,
            Diagnostics = diagnostics
        };
    }

    private async Task<JsonNode?> TryGet(
        Func<string, string, string?, CancellationToken, Task<JsonNode?>> fetch,
        string method,
        string path,
        CancellationToken cancellationToken,
        List<string> diagnostics,
        string label,
        ConversationEndpointKind kind)
    {
        try
        {
            return await fetch(method, path, null, cancellationToken);
        }
        catch (ChatGptProviderException ex) when (ex.Status is 404 or 400)
        {
            Capabilities.MarkUnsupported(kind);
            diagnostics.Add($"{label} unavailable status={ex.Status}");
            return null;
        }
    }

    private static bool HasMessages(JsonNode node) =>
        node["messages"] is JsonArray || node["items"] is JsonArray || node["turns"] is JsonArray;

    private static bool HasPageInfo(JsonNode node)
    {
        var info = ReadPageInfo(node);
        return info.HasPreviousPage is not null || !string.IsNullOrWhiteSpace(info.StartCursor);
    }

    private static void CollectMessages(JsonNode root, IDictionary<string, JsonObject> messages)
    {
        foreach (var item in ChatGptJson.Enumerate(root["messages"] ?? root["items"] ?? root["turns"]))
        {
            var node = NormalizeMessageNode(item);
            var id = ChatGptJson.GetString(node, "id") ?? ChatGptJson.GetString(node["message"], "id");
            if (string.IsNullOrWhiteSpace(id) || messages.ContainsKey(id))
            {
                continue;
            }

            StripBodies(node);
            messages[id] = node;
        }

        if (root["mapping"] is JsonObject mapping)
        {
            foreach (var property in mapping)
            {
                if (property.Value is JsonObject node && !messages.ContainsKey(property.Key))
                {
                    StripBodies(node);
                    messages[property.Key] = node;
                }
            }
        }
    }

    private static JsonObject NormalizeMessageNode(JsonNode item)
    {
        if (item is JsonObject obj && obj["message"] is JsonObject)
        {
            obj["parent"] ??= ChatGptJson.GetString(obj, "parent_id", "parent");
            obj["id"] ??= ChatGptJson.GetString(obj["message"], "id") ?? ChatGptJson.GetString(obj, "id");
            return obj;
        }

        var id = ChatGptJson.GetString(item, "id", "message_id") ?? Guid.NewGuid().ToString("N");
        var parent = ChatGptJson.GetString(item, "parent", "parent_id");
        var message = item["message"] as JsonObject ?? new JsonObject
        {
            ["id"] = id,
            ["author"] = item["author"]?.DeepClone() ?? new JsonObject { ["role"] = ChatGptJson.GetString(item, "role") ?? "" },
            ["create_time"] = TimestampParser.ToUnixSeconds(item, "create_time", "createTime"),
            ["end_turn"] = ChatGptJson.GetBool(item, "end_turn"),
            ["recipient"] = ChatGptJson.GetString(item, "recipient") ?? "all",
            ["metadata"] = item["metadata"]?.DeepClone() ?? new JsonObject(),
            ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray() }
        };
        return new JsonObject
        {
            ["id"] = id,
            ["parent"] = parent,
            ["children"] = new JsonArray(),
            ["message"] = message.DeepClone()
        };
    }

    private static (bool? HasPreviousPage, string? StartCursor) ReadPageInfo(JsonNode node)
    {
        var info = node["page_info"] ?? node["pageInfo"];
        return (
            ChatGptJson.GetBool(info, "has_previous_page", "hasPreviousPage"),
            ChatGptJson.GetString(info, "start_cursor", "startCursor"));
    }
}

public sealed class ConversationLoadResult
{
    public JsonNode? Conversation { get; init; }
    public bool Complete { get; init; }
    public bool SchemaMismatch { get; init; }
    public bool MappingUsed { get; init; }
    public bool PaginatedUsed { get; init; }
    public int PagesFetched { get; init; }
    public List<string> Diagnostics { get; init; } = [];
}
