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
            result.SchemaMismatch = true;
            result.Diagnostics.Add("mapping contained no nodes.");
            return result;
        }

        var groups = GroupAssistantNodes(conversationId, nodes);

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
                DedupeKey = dedupeKey,
                DedupeConfidence = group.All(n => !string.IsNullOrWhiteSpace(n.RequestId))
                    ? DedupeConfidence.High
                    : DedupeConfidence.Heuristic
            });
        }

        result.ConversationId = conversationId;
        result.UpdateTime = TimestampParser.ToUnixSeconds(conversation, "update_time", "updateTime");
        if (result.UpdateTime <= 0)
        {
            result.UpdateTime = context.UpdateTime;
        }

        return result;
    }

    private static Dictionary<string, List<ParsedNode>> GroupAssistantNodes(
        string conversationId,
        IReadOnlyDictionary<string, ParsedNode> nodes)
    {
        var assistants = nodes.Values.Where(IsAssistantLike).ToList();
        var groups = new Dictionary<string, List<ParsedNode>>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in assistants.Where(n => !string.IsNullOrWhiteSpace(n.RequestId)))
        {
            AddGroup(groups, "req:" + node.RequestId!.Trim(), node);
            assigned.Add(node.Id);
        }

        foreach (var node in assistants.Where(n => !assigned.Contains(n.Id)))
        {
            var tagged = FindTaggedGroup(groups, nodes, node);
            if (tagged is null)
            {
                continue;
            }

            AddGroup(groups, tagged, node);
            assigned.Add(node.Id);
        }

        var remaining = assistants.Where(n => !assigned.Contains(n.Id)).ToList();
        foreach (var family in remaining.GroupBy(n => FindUserAncestor(nodes, n) ?? n.ParentId ?? n.Id))
        {
            var members = family.ToList();
            var finals = members.Where(IsVisibleFinal).OrderBy(n => n.CreatedAt).ToList();
            var fragments = members.Where(n => !IsVisibleFinal(n)).ToList();
            if (finals.Count <= 1)
            {
                var key = "turn:" + conversationId + ":" + family.Key;
                foreach (var node in members)
                {
                    AddGroup(groups, key, node);
                }

                continue;
            }

            foreach (var final in finals)
            {
                AddGroup(groups, "turn:" + conversationId + ":" + family.Key + ":final:" + final.Id, final);
            }

            foreach (var fragment in fragments)
            {
                var nearest = finals
                    .OrderBy(final => Math.Abs((final.CreatedAt - fragment.CreatedAt)?.TotalSeconds ?? double.MaxValue))
                    .First();
                AddGroup(groups, "turn:" + conversationId + ":" + family.Key + ":final:" + nearest.Id, fragment);
            }
        }

        return groups;
    }

    private static string? FindTaggedGroup(
        IReadOnlyDictionary<string, List<ParsedNode>> groups,
        IReadOnlyDictionary<string, ParsedNode> nodes,
        ParsedNode node)
    {
        var user = FindUserAncestor(nodes, node);
        string? best = null;
        var bestDelta = double.MaxValue;
        foreach (var (key, members) in groups)
        {
            if (!key.StartsWith("req:", StringComparison.OrdinalIgnoreCase) || members.Count == 0)
            {
                continue;
            }

            if (user is null || members.All(member => FindUserAncestor(nodes, member) != user))
            {
                continue;
            }

            if (IsVisibleFinal(node) && members.Any(IsVisibleFinal))
            {
                continue;
            }

            if (!members.Any(member => CompatibleBranch(nodes, node, member)))
            {
                continue;
            }

            if (!members.Any(member => CompatibleModel(node, member)))
            {
                continue;
            }

            var delta = members.Min(member => Math.Abs((member.CreatedAt - node.CreatedAt)?.TotalSeconds ?? double.MaxValue));
            if (delta > 20 * 60)
            {
                continue;
            }

            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = key;
            }
        }

        return best;
    }

    private static bool CompatibleBranch(
        IReadOnlyDictionary<string, ParsedNode> nodes,
        ParsedNode left,
        ParsedNode right)
    {
        if (string.Equals(left.ParentId, right.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(right.ParentId, left.Id, StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.ParentId, right.ParentId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Ancestors(nodes, left).Contains(right.Id) || Ancestors(nodes, right).Contains(left.Id);
    }

    private static HashSet<string> Ancestors(IReadOnlyDictionary<string, ParsedNode> nodes, ParsedNode node)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = node.ParentId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && guard++ < 64)
        {
            ids.Add(cursor);
            if (!nodes.TryGetValue(cursor, out var parent))
            {
                break;
            }

            cursor = parent.ParentId;
        }

        return ids;
    }

    private static bool CompatibleModel(ParsedNode left, ParsedNode right)
    {
        var a = FirstNonEmpty(left.ModelSlug, left.ResponseModel, left.RequestedModel);
        var b = FirstNonEmpty(right.ModelSlug, right.ResponseModel, right.RequestedModel);
        return string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)
               || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddGroup(IDictionary<string, List<ParsedNode>> groups, string key, ParsedNode node)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            list = [];
            groups[key] = list;
        }

        list.Add(node);
    }

    private static string? FindUserAncestor(IReadOnlyDictionary<string, ParsedNode> nodes, ParsedNode node)
    {
        var cursor = node.ParentId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && guard++ < 64)
        {
            if (!nodes.TryGetValue(cursor, out var parent))
            {
                return cursor;
            }

            if (string.Equals(parent.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return parent.Id;
            }

            cursor = parent.ParentId;
        }

        return null;
    }

    private static bool IsVisibleFinal(ParsedNode node) =>
        !node.Hidden
        && node.EndTurn != false
        && (string.IsNullOrWhiteSpace(node.Recipient)
            || string.Equals(node.Recipient, "all", StringComparison.OrdinalIgnoreCase));

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
        var created = TimestampParser.ToDateTimeOffset(message, "create_time", "createTime")
                      ?? TimestampParser.ToDateTimeOffset(node, "create_time", "createTime");
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

    private static bool IsAssistantLike(ParsedNode node) =>
        string.Equals(node.Role, "assistant", StringComparison.OrdinalIgnoreCase);

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
