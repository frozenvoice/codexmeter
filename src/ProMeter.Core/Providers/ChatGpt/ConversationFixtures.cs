namespace ProMeter.Providers.ChatGpt;

public static class ConversationFixtures
{
    public static JsonObject Conversation(
        string id,
        double updateTime,
        params JsonObject[] nodes)
    {
        var mapping = new JsonObject();
        foreach (var node in nodes)
        {
            var nodeId = node["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N");
            mapping[nodeId] = node;
        }

        return new JsonObject
        {
            ["conversation_id"] = id,
            ["update_time"] = updateTime,
            ["create_time"] = updateTime - 10,
            ["mapping"] = mapping
        };
    }

    public static JsonObject Node(
        string id,
        string role,
        string? parent,
        double createTime,
        string? requestId = null,
        string? modelSlug = null,
        string? requestedModel = null,
        string? effort = null,
        bool hidden = false,
        bool endTurn = true,
        string? recipient = "all",
        IEnumerable<string>? children = null)
    {
        var metadata = new JsonObject();
        if (!string.IsNullOrWhiteSpace(requestId)) metadata["request_id"] = requestId;
        if (!string.IsNullOrWhiteSpace(modelSlug)) metadata["model_slug"] = modelSlug;
        if (!string.IsNullOrWhiteSpace(requestedModel)) metadata["default_model_slug"] = requestedModel;
        if (!string.IsNullOrWhiteSpace(effort)) metadata["thinking_effort"] = effort;
        if (hidden) metadata["is_visually_hidden_from_conversation"] = true;

        var message = new JsonObject
        {
            ["id"] = id,
            ["author"] = new JsonObject { ["role"] = role },
            ["create_time"] = createTime,
            ["end_turn"] = endTurn,
            ["recipient"] = recipient,
            ["content"] = new JsonObject { ["content_type"] = "text", ["parts"] = new JsonArray("[redacted]") },
            ["metadata"] = metadata
        };

        var childArray = new JsonArray();
        if (children is not null)
        {
            foreach (var child in children)
            {
                childArray.Add(child);
            }
        }

        return new JsonObject
        {
            ["id"] = id,
            ["parent"] = parent,
            ["children"] = childArray,
            ["message"] = message
        };
    }

    public static JsonObject NormalPro(string conversationId = "conv-normal", double time = 1_777_500_000)
    {
        return Conversation(
            conversationId,
            time,
            Node("root", "system", null, time - 3),
            Node("user-1", "user", "root", time - 2, requestedModel: "gpt-5-6-pro", children: ["asst-1"]),
            Node("asst-1", "assistant", "user-1", time - 1, requestId: "req-normal", modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject MultiAssistantSameRequest(string conversationId = "conv-multi", double time = 1_777_500_100)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 4, requestedModel: "gpt-5-6-pro", children: ["hidden", "reason", "final"]),
            Node("hidden", "assistant", "user-1", time - 3, requestId: "req-multi", modelSlug: "gpt-5-6-pro", hidden: true, endTurn: false),
            Node("reason", "assistant", "user-1", time - 2, requestId: "req-multi", modelSlug: "gpt-5-6-pro", hidden: true, endTurn: false),
            Node("final", "assistant", "user-1", time - 1, requestId: "req-multi", modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject Regenerated(string conversationId = "conv-regen", double time = 1_777_500_200)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 4, requestedModel: "gpt-5-6-pro", children: ["a", "b", "c"]),
            Node("a", "assistant", "user-1", time - 3, requestId: "req-a", modelSlug: "gpt-5-6-pro"),
            Node("b", "assistant", "user-1", time - 2, requestId: "req-b", modelSlug: "gpt-5-6-pro"),
            Node("c", "assistant", "user-1", time - 1, requestId: "req-c", modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject ToolCalls(string conversationId = "conv-tools", double time = 1_777_500_300)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 4, requestedModel: "gpt-5-6-pro", children: ["tool"]),
            Node("tool", "assistant", "user-1", time - 3, requestId: "req-tool", modelSlug: "gpt-5-6-pro", endTurn: false, recipient: "browser"),
            Node("tool-result", "tool", "tool", time - 2, requestId: "req-tool"),
            Node("final", "assistant", "tool-result", time - 1, requestId: "req-tool", modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject ReasoningMessage(string conversationId = "conv-reason", double time = 1_777_500_400)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 2, requestedModel: "gpt-5-6-thinking", children: ["asst-1"]),
            Node("asst-1", "assistant", "user-1", time - 1, requestId: "req-reason", modelSlug: "gpt-5-6-thinking", effort: "xhigh"));
    }

    public static JsonObject UnknownModel(string conversationId = "conv-unknown", double time = 1_777_500_500)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 2, requestedModel: "gpt-future-omega", children: ["asst-1"]),
            Node("asst-1", "assistant", "user-1", time - 1, requestId: "req-unknown", modelSlug: "gpt-future-omega"));
    }

    public static JsonObject MissingRequestId(string conversationId = "conv-noreq", double time = 1_777_500_600)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 2, requestedModel: "gpt-5-6-pro", children: ["asst-1"]),
            Node("asst-1", "assistant", "user-1", time - 1, modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject ProjectConversation(string conversationId = "conv-project", double time = 1_777_500_700)
    {
        var conversation = NormalPro(conversationId, time);
        conversation["gizmo_id"] = "gizmo-project";
        return conversation;
    }

    public static JsonObject ArchivedConversation(string conversationId = "conv-archived", double time = 1_777_500_800)
    {
        var conversation = NormalPro(conversationId, time);
        conversation["is_archived"] = true;
        return conversation;
    }

    public static string OfficialExportJson()
    {
        return new JsonArray
        {
            NormalPro("export-1", 1_777_501_000),
            Regenerated("export-2", 1_777_501_100)
        }.ToJsonString();
    }
}
