namespace CycleArc.Providers.ChatGpt;

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

        var current = nodes.Length == 0 ? null : ChatGptJson.GetString(nodes[^1], "id");
        return new JsonObject
        {
            ["conversation_id"] = id,
            ["update_time"] = updateTime,
            ["create_time"] = updateTime - 10,
            ["current_node"] = current,
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

    public static JsonObject TwoProTurns(string conversationId, double oldTime, double newTime)
    {
        return Conversation(
            conversationId,
            newTime,
            Node("root", "system", null, oldTime - 3, children: ["user-old"]),
            Node("user-old", "user", "root", oldTime - 2, requestedModel: "gpt-5-6-pro", children: ["asst-old"]),
            Node("asst-old", "assistant", "user-old", oldTime - 1, requestId: "req-old", modelSlug: "gpt-5-6-pro", children: ["user-new"]),
            Node("user-new", "user", "asst-old", newTime - 2, requestedModel: "gpt-5-6-pro", children: ["asst-new"]),
            Node("asst-new", "assistant", "user-new", newTime - 1, requestId: "req-new", modelSlug: "gpt-5-6-pro"));
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

    public static JsonObject Gpt6Pro(string conversationId = "conv-gpt6", double time = 1_777_500_640)
    {
        return Conversation(
            conversationId,
            time,
            Node("root", "system", null, time - 3),
            Node("user-1", "user", "root", time - 2, requestedModel: "gpt-6-pro", children: ["asst-1"]),
            Node("asst-1", "assistant", "user-1", time - 1, requestId: "req-gpt6", modelSlug: "gpt-6-pro"));
    }

    public static JsonObject LongConversation(string conversationId = "conv-long", double time = 1_777_500_680, int turns = 24)
    {
        var nodes = new List<JsonObject>
        {
            Node("root", "system", null, time - turns * 2 - 1)
        };
        var parent = "root";
        for (var i = 1; i <= turns; i++)
        {
            var userId = "user-" + i;
            var asstId = "asst-" + i;
            nodes.Add(Node(userId, "user", parent, time - (turns - i) * 2 - 1, requestedModel: "gpt-5-6-pro", children: [asstId]));
            nodes.Add(Node(asstId, "assistant", userId, time - (turns - i) * 2, requestId: "req-long-" + i, modelSlug: "gpt-5-6-pro"));
            parent = asstId;
        }

        return Conversation(conversationId, time, nodes.ToArray());
    }

    public static JsonObject MixedRequestIdFragments(string conversationId = "conv-mixed-req", double time = 1_777_500_655)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 4, requestedModel: "gpt-5-6-pro", children: ["hidden"]),
            Node("hidden", "assistant", "user-1", time - 3, modelSlug: "gpt-5-6-pro", hidden: true, endTurn: false, children: ["tool"]),
            Node("tool", "assistant", "hidden", time - 2, modelSlug: "gpt-5-6-pro", endTurn: false, recipient: "browser", children: ["final"]),
            Node("final", "assistant", "tool", time - 1, requestId: "req-mixed", modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject UnrelatedTurns(string conversationId = "conv-unrelated", double time = 1_777_500_656)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 6, requestedModel: "gpt-5-6-pro", children: ["hidden-1"]),
            Node("hidden-1", "assistant", "user-1", time - 5, modelSlug: "gpt-5-6-pro", hidden: true, endTurn: false),
            Node("user-2", "user", null, time - 3, requestedModel: "gpt-5-6-pro", children: ["final-2"]),
            Node("final-2", "assistant", "user-2", time - 1, requestId: "req-other", modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject MissingRequestIdFragments(string conversationId = "conv-noreq-cluster", double time = 1_777_500_650)
    {
        return Conversation(
            conversationId,
            time,
            Node("user-1", "user", null, time - 4, requestedModel: "gpt-5-6-pro", children: ["hidden"]),
            Node("hidden", "assistant", "user-1", time - 3, modelSlug: "gpt-5-6-pro", hidden: true, endTurn: false, children: ["tool"]),
            Node("tool", "assistant", "hidden", time - 2, modelSlug: "gpt-5-6-pro", endTurn: false, recipient: "browser", children: ["final"]),
            Node("final", "assistant", "tool", time - 1, modelSlug: "gpt-5-6-pro"));
    }

    public static JsonObject IncompleteMapping(string conversationId = "conv-incomplete", double time = 1_777_500_660)
    {
        var conversation = NormalPro(conversationId, time);
        conversation.Remove("current_node");
        return conversation;
    }

    public static JsonObject DirectPaginatedPro(
        string conversationId = "conv-direct-pro",
        double time = 1_777_500_900,
        string modelSlug = "gpt-5-6-pro",
        string requestId = "req-direct-pro")
    {
        return new JsonObject
        {
            ["conversation_id"] = conversationId,
            ["id"] = conversationId,
            ["update_time"] = time,
            ["current_node"] = "asst-1",
            ["messages"] = new JsonArray
            {
                DirectPaginatedMessage(
                    "user-1",
                    "user",
                    parent: null,
                    createTime: time - 2,
                    requestedModel: modelSlug,
                    parts: "SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE"),
                DirectPaginatedMessage(
                    "asst-1",
                    "assistant",
                    parent: "user-1",
                    createTime: time - 1,
                    requestId: requestId,
                    modelSlug: modelSlug,
                    parts: "SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE",
                    endTurn: true)
            },
            ["page_info"] = new JsonObject
            {
                ["has_previous_page"] = false,
                ["start_cursor"] = "synthetic-cursor-start"
            }
        };
    }

    public static JsonObject DirectPaginatedGpt6Pro(
        string conversationId = "conv-direct-gpt6",
        double time = 1_777_500_910) =>
        DirectPaginatedPro(conversationId, time, "gpt-6-pro", "req-direct-gpt6");

    public static JsonObject DirectPaginatedMessage(
        string id,
        string role,
        string? parent,
        double createTime,
        string? requestId = null,
        string? modelSlug = null,
        string? requestedModel = null,
        string? parts = null,
        bool endTurn = true,
        bool useAliases = false)
    {
        var metadata = new JsonObject();
        if (!string.IsNullOrWhiteSpace(requestId)) metadata["request_id"] = requestId;
        if (!string.IsNullOrWhiteSpace(modelSlug)) metadata["model_slug"] = modelSlug;
        if (!string.IsNullOrWhiteSpace(requestedModel)) metadata["requested_model"] = requestedModel;

        var node = new JsonObject
        {
            [useAliases ? "message_id" : "id"] = id,
            ["author"] = new JsonObject { ["role"] = role },
            ["create_time"] = createTime,
            ["end_turn"] = endTurn,
            ["recipient"] = "all",
            ["content"] = new JsonObject
            {
                ["content_type"] = "text",
                ["parts"] = new JsonArray(parts ?? "[redacted]"),
                ["text"] = parts ?? ""
            },
            ["metadata"] = metadata
        };
        if (!string.IsNullOrWhiteSpace(parent))
        {
            node[useAliases ? "parent_id" : "parent"] = parent;
        }

        return node;
    }

    public static JsonObject MappingWithStrippedAssistantRoles(string conversationId = "conv-stripped", double time = 1_777_500_920)
    {
        return Conversation(
            conversationId,
            time,
            new JsonObject
            {
                ["id"] = "user-1",
                ["parent"] = null,
                ["children"] = new JsonArray("asst-1"),
                ["message"] = new JsonObject
                {
                    ["id"] = "user-1",
                    ["author"] = new JsonObject { ["role"] = "" },
                    ["create_time"] = time - 2,
                    ["content"] = new JsonObject { ["content_type"] = "text" },
                    ["metadata"] = new JsonObject()
                }
            },
            new JsonObject
            {
                ["id"] = "asst-1",
                ["parent"] = "user-1",
                ["children"] = new JsonArray(),
                ["message"] = new JsonObject
                {
                    ["id"] = "asst-1",
                    ["author"] = new JsonObject { ["role"] = "" },
                    ["create_time"] = time - 1,
                    ["end_turn"] = true,
                    ["recipient"] = "all",
                    ["content"] = new JsonObject { ["content_type"] = "text" },
                    ["metadata"] = new JsonObject()
                }
            });
    }

    public static JsonObject PaginatedHead(string conversationId = "conv-paged", double time = 1_777_500_670) =>
        new()
        {
            ["conversation_id"] = conversationId,
            ["update_time"] = time,
            ["current_node"] = "final",
            ["messages"] = new JsonArray
            {
                Node("final", "assistant", "older-user", time, modelSlug: "gpt-5-6-pro", requestId: "req-page-2")
            },
            ["page_info"] = new JsonObject
            {
                ["has_previous_page"] = true,
                ["start_cursor"] = "cursor-older"
            }
        };

    public static JsonObject PaginatedOlder(string conversationId = "conv-paged", double time = 1_777_500_670) =>
        new()
        {
            ["conversation_id"] = conversationId,
            ["messages"] = new JsonArray
            {
                Node("older-user", "user", null, time - 2, requestedModel: "gpt-5-6-pro", children: ["final"]),
                Node("older-asst", "assistant", "older-user", time - 1, requestId: "req-page-1", modelSlug: "gpt-5-6-pro")
            },
            ["page_info"] = new JsonObject
            {
                ["has_previous_page"] = false,
                ["start_cursor"] = "cursor-start"
            }
        };

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
