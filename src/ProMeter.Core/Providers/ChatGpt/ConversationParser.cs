namespace ProMeter.Providers.ChatGpt;

public sealed class ConversationParser
{
    private readonly ModelNormalizer _models;

    public ConversationParser(ModelNormalizer models)
    {
        _models = models;
    }

    public ParseResult Parse(JsonNode? conversation, ConversationParseContext context)
    {
        var result = new ParseResult();
        if (conversation is null)
        {
            result.SchemaMismatch = true;
            result.Diagnostics.Add("Conversation JSON was null.");
            return result;
        }

        var conversationId = ChatGptJson.GetString(conversation, "conversation_id", "id") ?? context.ConversationId;
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            result.SchemaMismatch = true;
            result.Diagnostics.Add("Conversation id missing.");
            return result;
        }

        var mapping = conversation["mapping"] as JsonObject;
        if (mapping is null)
        {
            result.SchemaMismatch = true;
            result.Diagnostics.Add("mapping object missing.");
            return result;
        }

        result.HasAlternateBranches = DetectAlternateBranches(mapping);
        var nodes = new Dictionary<string, ParsedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in mapping)
        {
            if (property.Value is not JsonObject node)
            {
                continue;
            }

            var parsed = ReadNode(property.Key, node);
            nodes[parsed.Id] = parsed;
        }

        if (nodes.Count == 0)
        {
            result.Diagnostics.Add("mapping contained no nodes.");
            return result;
        }

        var groups = new Dictionary<string, List<ParsedNode>>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Values)
        {
            if (!IsAssistantLike(node))
            {
                continue;
            }

            var key = UsageEvent.BuildDedupeKey(conversationId, node.RequestId, FallbackMessageKey(node));
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(node);
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (dedupeKey, group) in groups)
        {
            var representative = SelectRepresentative(group);
            if (representative is null)
            {
                continue;
            }

            var requested = FirstNonEmpty(
                representative.RequestedModel,
                FindParentRequestedModel(nodes, representative),
                context.DefaultModelSlug);
            var response = FirstNonEmpty(representative.ResponseModel, representative.ModelSlug);
            var normalized = _models.Resolve(requested, response);
            var effort = representative.Effort;
            if (effort == ReasoningEffort.Unknown)
            {
                foreach (var node in group)
                {
                    if (node.Effort != ReasoningEffort.Unknown)
                    {
                        effort = node.Effort;
                        break;
                    }
                }
            }

            var family = RefineFamily(normalized.Family, effort, normalized.RawSlug);
            var created = representative.CreatedAt ?? context.FallbackCreatedAt ?? now;

            result.Events.Add(new UsageEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                RequestId = representative.RequestId,
                ConversationId = conversationId,
                MessageId = representative.MessageId,
                CreatedAt = created,
                RequestedModel = requested,
                ResponseModel = response,
                NormalizedModel = normalized.DisplayName,
                RawModel = string.IsNullOrWhiteSpace(normalized.RawSlug) ? (response ?? requested ?? "") : normalized.RawSlug,
                ReasoningEffort = effort,
                Source = context.Source,
                ProjectId = context.ProjectId,
                IsArchived = context.Archived,
                FirstSeenAt = now,
                LastSeenAt = now,
                QuotaFamily = family,
                DedupeKey = dedupeKey
            });
        }

        result.ConversationId = conversationId;
        result.UpdateTime = ChatGptJson.GetDouble(conversation, "update_time", "updateTime") ?? context.UpdateTime;
        return result;
    }

    public static bool DetectAlternateBranches(JsonObject mapping)
    {
        foreach (var property in mapping)
        {
            if (property.Value?["children"] is JsonArray children && children.Count > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static ParsedNode ReadNode(string fallbackId, JsonObject node)
    {
        var message = node["message"] as JsonObject;
        var metadata = message?["metadata"] as JsonObject;
        var author = message?["author"] as JsonObject;
        var id = ChatGptJson.GetString(node, "id") ?? ChatGptJson.GetString(message, "id") ?? fallbackId;
        var created = UnixToDate(ChatGptJson.GetDouble(message, "create_time") ?? ChatGptJson.GetDouble(node, "create_time"));
        var role = ChatGptJson.GetString(author, "role") ?? "";
        var requestId = ChatGptJson.GetString(metadata, "request_id", "requestId");
        var modelSlug = ChatGptJson.GetString(metadata, "model_slug", "resolved_model_slug", "default_model_slug");
        var requested = ChatGptJson.GetString(metadata, "requested_model", "requested_model_slug", "default_model_slug");
        var hidden = ChatGptJson.GetBool(metadata, "is_visually_hidden_from_conversation") == true;
        var endTurn = ChatGptJson.GetBool(message, "end_turn");
        var contentType = ChatGptJson.GetString(message?["content"], "content_type");
        var recipient = ChatGptJson.GetString(message, "recipient");

        return new ParsedNode
        {
            Id = id,
            ParentId = ChatGptJson.GetString(node, "parent") ?? ChatGptJson.GetString(metadata, "parent_id"),
            MessageId = ChatGptJson.GetString(message, "id") ?? id,
            Role = role,
            RequestId = requestId,
            ModelSlug = modelSlug,
            RequestedModel = requested,
            ResponseModel = modelSlug,
            Effort = ReasoningNormalizer.FromMetadata(metadata),
            CreatedAt = created,
            Hidden = hidden,
            EndTurn = endTurn,
            ContentType = contentType,
            Recipient = recipient
        };
    }

    private static bool IsAssistantLike(ParsedNode node)
    {
        if (!string.Equals(node.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(node.Recipient, "all", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(node.Recipient))
        {
            return true;
        }

        // Tool-bound assistant fragments still belong to a request, but
        // they must not create extra events when request_id is absent.
        return !string.IsNullOrWhiteSpace(node.RequestId);
    }

    private static ParsedNode? SelectRepresentative(List<ParsedNode> group)
    {
        return group
            .OrderBy(n => n.Hidden)
            .ThenByDescending(n => n.EndTurn == true)
            .ThenByDescending(n => n.CreatedAt)
            .ThenBy(n => n.MessageId)
            .FirstOrDefault();
    }

    private static string? FindParentRequestedModel(IReadOnlyDictionary<string, ParsedNode> nodes, ParsedNode node)
    {
        if (string.IsNullOrWhiteSpace(node.ParentId) || !nodes.TryGetValue(node.ParentId, out var parent))
        {
            return null;
        }

        if (string.Equals(parent.Role, "user", StringComparison.OrdinalIgnoreCase))
        {
            return FirstNonEmpty(parent.RequestedModel, parent.ModelSlug);
        }

        return null;
    }

    private static string FallbackMessageKey(ParsedNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.MessageId))
        {
            return node.MessageId;
        }

        return $"{node.ParentId}:{node.CreatedAt?.ToUnixTimeSeconds()}:{node.ModelSlug}";
    }

    private static QuotaFamily RefineFamily(QuotaFamily family, ReasoningEffort effort, string rawSlug)
    {
        if (family == QuotaFamily.GptPro)
        {
            return QuotaFamily.GptPro;
        }

        if (family == QuotaFamily.SolReasoning)
        {
            return QuotaFamily.SolReasoning;
        }

        if (family == QuotaFamily.Instant && effort is ReasoningEffort.Medium or ReasoningEffort.High or ReasoningEffort.ExtraHigh)
        {
            return QuotaFamily.SolReasoning;
        }

        if (family == QuotaFamily.Unknown
            && effort is ReasoningEffort.Medium or ReasoningEffort.High or ReasoningEffort.ExtraHigh
            && rawSlug.Contains("5-6", StringComparison.OrdinalIgnoreCase))
        {
            return QuotaFamily.SolReasoning;
        }

        return family;
    }

    private static DateTimeOffset? UnixToDate(double? unix)
    {
        if (unix is null or <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(unix.Value * 1000d));
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private sealed class ParsedNode
    {
        public string Id { get; set; } = "";
        public string? ParentId { get; set; }
        public string? MessageId { get; set; }
        public string Role { get; set; } = "";
        public string? RequestId { get; set; }
        public string? ModelSlug { get; set; }
        public string? RequestedModel { get; set; }
        public string? ResponseModel { get; set; }
        public ReasoningEffort Effort { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public bool Hidden { get; set; }
        public bool? EndTurn { get; set; }
        public string? ContentType { get; set; }
        public string? Recipient { get; set; }
    }
}

public sealed class ConversationParseContext
{
    public string ConversationId { get; set; } = "";
    public string? ProjectId { get; set; }
    public bool Archived { get; set; }
    public UsageSource Source { get; set; } = UsageSource.ConversationSync;
    public double UpdateTime { get; set; }
    public string? DefaultModelSlug { get; set; }
    public DateTimeOffset? FallbackCreatedAt { get; set; }
}

public sealed class ParseResult
{
    public string ConversationId { get; set; } = "";
    public double UpdateTime { get; set; }
    public bool SchemaMismatch { get; set; }
    public bool HasAlternateBranches { get; set; }
    public List<UsageEvent> Events { get; } = [];
    public List<string> Diagnostics { get; } = [];
}
