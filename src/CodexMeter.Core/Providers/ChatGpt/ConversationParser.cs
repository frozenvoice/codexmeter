using CodexMeter.Services;

namespace CodexMeter.Providers.ChatGpt;

public sealed class ConversationParser
{
    private readonly ModelNormalizer _models;
    private readonly IClock _clock;

    public ConversationParser(ModelNormalizer models, IClock? clock = null)
    {
        _models = models;
        _clock = clock ?? SystemClock.Instance;
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

        context.ConversationId = conversationId;
        var mapping = conversation["mapping"] as JsonObject;
        if (mapping is null)
        {
            result.SchemaMismatch = true;
            result.Diagnostics.Add("mapping object missing.");
            return result;
        }

        result.HasAlternateBranches = DetectAlternateBranches(mapping);
        var observations = new List<UsageObservation>();
        foreach (var property in mapping)
        {
            if (property.Value is not JsonObject node)
            {
                continue;
            }

            observations.Add(ReadObservation(property.Key, node, context, _clock.UtcNow));
        }

        if (observations.Count == 0)
        {
            result.SchemaMismatch = true;
            result.Diagnostics.Add("mapping contained no nodes.");
            return result;
        }

        result.MappingNodeCount = observations.Count;
        result.AssistantLikeNodeCount = observations.Count(n => string.Equals(n.Role, "assistant", StringComparison.OrdinalIgnoreCase));
        result.NodesWithModelMetadata = observations.Count(HasModelMetadata);
        result.NodesMissingRole = observations.Count(n => n.HasMessage && string.IsNullOrWhiteSpace(n.Role));
        result.Observations.AddRange(observations);
        result.Events.AddRange(RequestCanonicalizer.Canonicalize(observations, _models, context, _clock));

        result.ConversationId = conversationId;
        result.UpdateTime = TimestampParser.ToUnixSeconds(conversation, "update_time", "updateTime");
        if (result.UpdateTime <= 0)
        {
            result.UpdateTime = context.UpdateTime;
        }

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

    private static UsageObservation ReadObservation(string fallbackId, JsonObject node, ConversationParseContext context, DateTimeOffset observedAt)
    {
        var message = node["message"] as JsonObject;
        var metadata = message?["metadata"] as JsonObject;
        var author = message?["author"] as JsonObject;
        var id = ChatGptJson.GetString(node, "id") ?? ChatGptJson.GetString(message, "id") ?? fallbackId;
        var created = TimestampParser.ToDateTimeOffset(message, "create_time", "createTime")
                      ?? TimestampParser.ToDateTimeOffset(node, "create_time", "createTime");
        var role = ChatGptJson.GetString(author, "role") ?? "";
        var requestId = ChatGptJson.GetString(metadata, "request_id", "requestId");
        var modelSlug = ChatGptJson.GetString(metadata, "model_slug", "resolved_model_slug", "default_model_slug");
        var requested = ChatGptJson.GetString(metadata, "requested_model", "requested_model_slug", "default_model_slug");
        var hidden = ChatGptJson.GetBool(metadata, "is_visually_hidden_from_conversation") == true;
        var endTurn = ChatGptJson.GetBool(message, "end_turn");
        var recipient = ChatGptJson.GetString(message, "recipient");

        return new UsageObservation
        {
            Id = id,
            ConversationId = context.ConversationId,
            ParentMessageId = ChatGptJson.GetString(node, "parent") ?? ChatGptJson.GetString(metadata, "parent_id"),
            MessageId = ChatGptJson.GetString(message, "id") ?? id,
            Role = role,
            RequestId = requestId,
            RequestedModel = requested,
            ResponseModel = modelSlug,
            RawModel = modelSlug,
            Effort = ReasoningNormalizer.FromMetadata(metadata),
            CreatedAt = created,
            Hidden = hidden,
            EndTurn = endTurn,
            Recipient = recipient,
            Source = context.Source,
            ProjectId = context.ProjectId,
            IsArchived = context.Archived,
            ObservedAt = observedAt,
            ReconstructionVersion = ConversationFetchBackoff.ReconstructionSemanticsVersion,
            HasMessage = message is not null
        };
    }

    private static bool HasModelMetadata(UsageObservation node) =>
        !string.IsNullOrWhiteSpace(node.RawModel)
        || !string.IsNullOrWhiteSpace(node.RequestedModel)
        ||         !string.IsNullOrWhiteSpace(node.ResponseModel);
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
    public int MappingNodeCount { get; set; }
    public int AssistantLikeNodeCount { get; set; }
    public int NodesWithModelMetadata { get; set; }
    public int NodesMissingRole { get; set; }
    public List<UsageEvent> Events { get; } = [];
    public List<UsageObservation> Observations { get; } = [];
    public List<string> Diagnostics { get; } = [];

    public bool LoadedWithoutAssistantUsage =>
        !SchemaMismatch && Events.Count == 0 && MappingNodeCount > 0;
}
