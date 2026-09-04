namespace ProMeter.Providers.ChatGpt;

public static class BridgeProjection
{
    public static readonly HashSet<string> ConversationMetadataKeys = new(StringComparer.Ordinal)
    {
        "request_id",
        "requestId",
        "model_slug",
        "resolved_model_slug",
        "requested_model",
        "requested_model_slug",
        "default_model_slug",
        "reasoning_effort",
        "thinking_effort",
        "effort",
        "reasoningEffort",
        "thinkingEffort",
        "is_visually_hidden_from_conversation",
        "parent_id",
        "model_experience"
    };

    public static JsonNode? Project(CompanionOperation operation, JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return operation switch
        {
            CompanionOperation.GetSessionStatus => ProjectSession(node),
            CompanionOperation.GetAccountCheck => ProjectAccountCheck(node),
            CompanionOperation.GetAccountMe => ProjectAccountMe(node),
            CompanionOperation.GetModels => ProjectModels(node),
            CompanionOperation.GetConversationIndex or CompanionOperation.GetArchivedConversationIndex
                or CompanionOperation.GetProjectConversations => ProjectConversationIndex(node),
            CompanionOperation.GetProjects => ProjectProjects(node),
            CompanionOperation.GetConversationHead or CompanionOperation.GetConversationFull
                or CompanionOperation.GetConversationLegacy or CompanionOperation.GetOlderConversationMessages
                => ProjectConversationDetail(node),
            CompanionOperation.GetQuotaInit => ProjectQuota(node),
            _ => null
        };
    }

    public static bool TryValidateProjected(CompanionOperation operation, JsonNode? node, out string error)
    {
        error = "";
        if (ContainsSecret(node))
        {
            error = "secret field leaked through bridge";
            return false;
        }

        if (ContainsPromptOrResponseText(node))
        {
            error = "prompt or response text leaked through bridge";
            return false;
        }

        if (operation is CompanionOperation.GetConversationIndex
                or CompanionOperation.GetArchivedConversationIndex
                or CompanionOperation.GetProjectConversations
                or CompanionOperation.GetConversationHead
                or CompanionOperation.GetConversationFull
                or CompanionOperation.GetConversationLegacy
                or CompanionOperation.GetOlderConversationMessages
            && ContainsConversationTitle(node))
        {
            error = "conversation title leaked through bridge";
            return false;
        }

        return true;
    }

    public static bool ContainsPromptOrResponseText(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["content"] is JsonObject content)
            {
                if (content["parts"] is JsonArray { Count: > 0 } || HasNonEmptyString(content["text"]))
                {
                    return true;
                }
            }

            foreach (var key in new[] { "transcript", "caption", "captions", "file_name", "filename", "attachment", "attachments", "tool_calls", "arguments" })
            {
                if (obj[key] is not null)
                {
                    return true;
                }
            }

            return obj.Any(property => ContainsPromptOrResponseText(property.Value));
        }

        return node is JsonArray array && array.Any(ContainsPromptOrResponseText);
    }

    private static bool ContainsConversationTitle(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (HasNonEmptyString(obj["title"]))
            {
                return true;
            }

            return obj.Any(property => ContainsConversationTitle(property.Value));
        }

        return node is JsonArray array && array.Any(ContainsConversationTitle);
    }

    private static JsonNode ProjectSession(JsonNode node)
    {
        var user = node["user"] as JsonObject ?? node as JsonObject;
        var projected = new JsonObject
        {
            ["signedIn"] = !string.IsNullOrWhiteSpace(ChatGptJson.GetString(user, "id", "email"))
        };
        if (user is not null)
        {
            projected["user"] = Pick(user, "id", "email", "name");
        }

        CopyIfPresent(node as JsonObject, projected, "expires", "expires_at", "expiresAt");
        return projected;
    }

    private static JsonNode ProjectAccountMe(JsonNode node) =>
        Pick(node as JsonObject, "id", "email", "name");

    private static JsonNode ProjectAccountCheck(JsonNode node)
    {
        var result = new JsonObject();
        if (node["accounts"] is JsonObject accounts)
        {
            var projectedAccounts = new JsonObject();
            foreach (var property in accounts)
            {
                projectedAccounts[property.Key] = ProjectAccount(property.Value as JsonObject);
            }

            result["accounts"] = projectedAccounts;
        }

        return result;
    }

    private static JsonNode ProjectAccount(JsonObject? account)
    {
        var result = new JsonObject();
        if (account?["entitlement"] is JsonObject entitlement)
        {
            result["entitlement"] = Pick(entitlement, "subscription_plan", "plan_type", "has_active_subscription");
        }

        if (account?["account"] is JsonObject inner)
        {
            result["account"] = Pick(inner, "plan_type", "planType");
        }

        return result;
    }

    private static JsonNode ProjectModels(JsonNode node)
    {
        if (node["models"] is JsonArray models)
        {
            return new JsonObject { ["models"] = ProjectModelArray(models) };
        }

        if (node is JsonArray array)
        {
            return ProjectModelArray(array);
        }

        if (node["categories"] is JsonArray categories)
        {
            var projected = new JsonArray();
            foreach (var category in ChatGptJson.Enumerate(categories))
            {
                if (category["models"] is JsonArray nested)
                {
                    projected.Add(new JsonObject { ["models"] = ProjectModelArray(nested) });
                }
            }

            return new JsonObject { ["categories"] = projected };
        }

        return new JsonObject();
    }

    private static JsonArray ProjectModelArray(JsonArray models)
    {
        var projected = new JsonArray();
        foreach (var model in ChatGptJson.Enumerate(models))
        {
            if (model is JsonObject obj)
            {
                projected.Add(Pick(obj, "slug", "id", "model", "model_slug", "title", "display_name", "name", "description", "tags"));
            }
        }

        return projected;
    }

    private static JsonNode ProjectConversationIndex(JsonNode node)
    {
        var result = new JsonObject();
        CopyIfPresent(node as JsonObject, result, "total", "total_count", "offset", "limit", "next_offset", "nextOffset", "has_more", "hasMore", "next_cursor", "cursor", "nextCursor");
        var items = AccountParser.ConversationIndexArray(node);
        if (items is not null)
        {
            var key = node["items"] is JsonArray ? "items" : node["conversations"] is JsonArray ? "conversations" : "items";
            var projected = new JsonArray();
            foreach (var item in ChatGptJson.Enumerate(items))
            {
                if (item is JsonObject obj)
                {
                    projected.Add(Pick(obj, "id", "conversation_id", "create_time", "createTime", "update_time", "updateTime", "is_archived", "archived", "gizmo_id", "project_id"));
                }
            }

            result[key] = projected;
        }

        return result;
    }

    private static JsonNode ProjectProjects(JsonNode node)
    {
        var result = new JsonObject();
        CopyIfPresent(node as JsonObject, result, "has_more", "hasMore", "next_cursor", "cursor", "nextCursor");
        var items = AccountParser.ProjectIndexArray(node);
        if (items is not null)
        {
            var key = node["gizmos"] is JsonArray ? "gizmos" : "items";
            var projected = new JsonArray();
            foreach (var item in ChatGptJson.Enumerate(items))
            {
                projected.Add(ProjectProjectItem(item as JsonObject));
            }

            result[key] = projected;
        }

        return result;
    }

    private static JsonNode ProjectProjectItem(JsonObject? item)
    {
        var result = new JsonObject();
        if (item is null)
        {
            return result;
        }

        CopyIfPresent(item, result, "id");
        if (item["gizmo"] is JsonObject gizmo)
        {
            result["gizmo"] = ProjectGizmo(gizmo);
        }

        return result;
    }

    private static JsonObject ProjectGizmo(JsonObject gizmo)
    {
        if (gizmo["gizmo"] is JsonObject nested)
        {
            return new JsonObject { ["gizmo"] = ProjectGizmo(nested) };
        }

        var result = Pick(gizmo, "id", "name", "title");
        if (gizmo["display"] is JsonObject display)
        {
            result["display"] = Pick(display, "name");
        }

        return result;
    }

    private static JsonNode ProjectConversationDetail(JsonNode node)
    {
        var result = new JsonObject();
        CopyIfPresent(node as JsonObject, result, "conversation_id", "id", "current_node", "update_time", "updateTime");
        if (node["page_info"] is JsonObject pageInfo)
        {
            result["page_info"] = Pick(pageInfo, "has_previous_page", "hasPreviousPage", "start_cursor", "startCursor");
        }
        else if (node["pageInfo"] is JsonObject pageInfoCamel)
        {
            result["page_info"] = Pick(pageInfoCamel, "has_previous_page", "hasPreviousPage", "start_cursor", "startCursor");
        }

        if (node["mapping"] is JsonObject mapping)
        {
            var projectedMapping = new JsonObject();
            foreach (var property in mapping)
            {
                if (property.Value is JsonObject value)
                {
                    projectedMapping[property.Key] = ProjectMappingNode(value);
                }
            }

            result["mapping"] = projectedMapping;
        }

        foreach (var key in new[] { "messages", "items", "turns" })
        {
            if (node[key] is JsonArray array)
            {
                var projected = new JsonArray();
                foreach (var item in array)
                {
                    if (item is JsonObject obj)
                    {
                        projected.Add(ProjectMappingNode(obj));
                    }
                }

                result[key] = projected;
            }
        }

        return result;
    }

    private static JsonObject ProjectMappingNode(JsonObject node)
    {
        var result = new JsonObject();
        CopyIfPresent(node, result, "id", "parent", "parent_id");
        if (node["children"] is JsonArray children)
        {
            var projectedChildren = new JsonArray();
            foreach (var child in children)
            {
                if (child is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                {
                    projectedChildren.Add(value.GetValue<string>());
                }
            }

            result["children"] = projectedChildren;
        }

        if (node["message"] is JsonObject message)
        {
            result["message"] = ProjectMessage(message);
        }

        CopyIfPresent(node, result, "create_time", "createTime");
        return result;
    }

    private static JsonObject ProjectMessage(JsonObject message)
    {
        var result = new JsonObject();
        CopyIfPresent(message, result, "id", "create_time", "createTime", "end_turn", "recipient");
        if (message["author"] is JsonObject author)
        {
            result["author"] = Pick(author, "role");
        }

        if (message["content"] is JsonObject content)
        {
            result["content"] = Pick(content, "content_type");
        }

        if (message["metadata"] is JsonObject metadata)
        {
            result["metadata"] = ProjectMetadata(metadata);
        }

        return result;
    }

    private static JsonObject ProjectMetadata(JsonObject metadata)
    {
        var result = new JsonObject();
        foreach (var key in ConversationMetadataKeys)
        {
            if (metadata[key] is null)
            {
                continue;
            }

            if (key == "model_experience" && metadata[key] is JsonObject experience)
            {
                result[key] = Pick(experience, "reasoning_effort", "thinking_effort", "effort");
                continue;
            }

            result[key] = metadata[key]!.DeepClone();
        }

        return result;
    }

    private static JsonNode ProjectQuota(JsonNode node)
    {
        var result = new JsonObject();
        if (node["limits_progress"] is JsonArray limits)
        {
            result["limits_progress"] = ProjectQuotaArray(limits, "feature_name", "name");
        }

        if (node["model_limits"] is JsonArray models)
        {
            result["model_limits"] = ProjectQuotaArray(models, "slug", "model", "model_slug", "name");
        }

        return result;
    }

    private static JsonArray ProjectQuotaArray(JsonArray items, params string[] nameKeys)
    {
        var projected = new JsonArray();
        foreach (var item in ChatGptJson.Enumerate(items))
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var entry = Pick(obj, nameKeys.Concat(["used", "limit", "resets_at", "reset_at", "resetAt", "resetsAt", "period", "window"]).ToArray());
            projected.Add(entry);
        }

        return projected;
    }

    private static JsonObject Pick(JsonObject? source, params string[] keys)
    {
        var result = new JsonObject();
        if (source is null)
        {
            return result;
        }

        foreach (var key in keys)
        {
            if (source[key] is JsonNode node)
            {
                result[key] = node.DeepClone();
            }
        }

        return result;
    }

    private static void CopyIfPresent(JsonObject? source, JsonObject target, params string[] keys)
    {
        if (source is null)
        {
            return;
        }

        foreach (var key in keys)
        {
            if (source[key] is JsonNode node)
            {
                target[key] = node.DeepClone();
            }
        }
    }

    private static bool ContainsSecret(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "accessToken", "access_token", "sessionToken", "session_token", "authorization", "cookie" })
            {
                if (obj[key] is not null)
                {
                    return true;
                }
            }

            return obj.Any(property => ContainsSecret(property.Value));
        }

        return node is JsonArray array && array.Any(ContainsSecret);
    }

    private static bool HasNonEmptyString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.ToString());
}
