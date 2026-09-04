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
        if (node is null)
        {
            error = "projected body missing";
            return false;
        }

        if (ContainsSecret(node))
        {
            error = "secret field leaked through bridge";
            return false;
        }

        return operation switch
        {
            CompanionOperation.GetSessionStatus => IsSession(node, out error),
            CompanionOperation.GetAccountMe => IsAccountMe(node, out error),
            CompanionOperation.GetAccountCheck => IsAccountCheck(node, out error),
            CompanionOperation.GetModels => IsModels(node, out error),
            CompanionOperation.GetConversationIndex or CompanionOperation.GetArchivedConversationIndex
                or CompanionOperation.GetProjectConversations => IsConversationIndex(node, out error),
            CompanionOperation.GetProjects => IsProjects(node, out error),
            CompanionOperation.GetConversationHead or CompanionOperation.GetConversationFull
                or CompanionOperation.GetConversationLegacy or CompanionOperation.GetOlderConversationMessages
                => IsConversationDetail(node, out error),
            CompanionOperation.GetQuotaInit => IsQuota(node, out error),
            _ => Fail("unknown operation", out error)
        };
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

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool IsSession(JsonNode node, out string error)
    {
        if (!ExpectObject(node, ["signedIn", "user", "expires", "expires_at", "expiresAt"], 8, out var obj, out error))
        {
            return false;
        }

        if (obj["signedIn"] is not null && !IsBool(obj["signedIn"]))
        {
            return Fail("signedIn must be boolean", out error);
        }

        if (obj["user"] is not null
            && !ExpectObject(obj["user"], ["id", "email", "name"], 3, out _, out error, requireStrings: true))
        {
            return false;
        }

        return OptionalStrings(obj, ["expires", "expires_at", "expiresAt"], out error);
    }

    private static bool IsAccountMe(JsonNode node, out string error) =>
        ExpectObject(node, ["id", "email", "name"], 3, out _, out error, requireStrings: true);

    private static bool IsAccountCheck(JsonNode node, out string error)
    {
        if (!ExpectObject(node, ["accounts"], 32, out var obj, out error))
        {
            return false;
        }

        if (obj["accounts"] is null)
        {
            return true;
        }

        if (obj["accounts"] is not JsonObject accounts || accounts.Count > 32)
        {
            return Fail("accounts shape rejected", out error);
        }

        foreach (var account in accounts)
        {
            if (!ExpectObject(account.Value, ["entitlement", "account"], 2, out var item, out error))
            {
                return false;
            }

            if (item["entitlement"] is not null
                && !ExpectObject(item["entitlement"], ["subscription_plan", "plan_type", "has_active_subscription"], 3, out _, out error))
            {
                return false;
            }
            else if (item["entitlement"] is JsonObject entitlementObj)
            {
                if (entitlementObj["has_active_subscription"] is not null && !IsBool(entitlementObj["has_active_subscription"]))
                {
                    return Fail("has_active_subscription must be boolean", out error);
                }

                if (!OptionalStrings(entitlementObj, ["subscription_plan", "plan_type"], out error))
                {
                    return false;
                }
            }

            if (item["account"] is not null
                && !ExpectObject(item["account"], ["plan_type", "planType"], 2, out _, out error, requireStrings: true))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsModels(JsonNode node, out string error)
    {
        if (node is JsonArray array)
        {
            return IsModelArray(array, out error);
        }

        if (!ExpectObject(node, ["models", "categories"], 2, out var obj, out error))
        {
            return false;
        }

        if (obj["models"] is not null)
        {
            if (obj["models"] is not JsonArray models)
            {
                return Fail("models must be an array", out error);
            }

            if (!IsModelArray(models, out error))
            {
                return false;
            }
        }

        if (obj["categories"] is not null)
        {
            if (obj["categories"] is not JsonArray categories)
            {
                return Fail("categories must be an array", out error);
            }

            if (categories.Count > 64)
            {
                return Fail("categories too large", out error);
            }

            foreach (var category in categories)
            {
                if (!ExpectObject(category, ["models"], 1, out var cat, out error))
                {
                    return false;
                }

                if (cat["models"] is not null)
                {
                    if (cat["models"] is not JsonArray nested)
                    {
                        return Fail("models must be an array", out error);
                    }

                    if (!IsModelArray(nested, out error))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool IsModelArray(JsonArray models, out string error)
    {
        error = "";
        if (models.Count > 500)
        {
            return Fail("models too large", out error);
        }

        foreach (var model in models)
        {
            if (!ExpectObject(model, ["slug", "id", "model", "model_slug", "title", "display_name", "name", "description", "tags"], 9, out var obj, out error))
            {
                return false;
            }

            if (obj["tags"] is not null && obj["tags"] is not JsonArray && !IsString(obj["tags"]))
            {
                return Fail("model tags rejected", out error);
            }

            if (obj["tags"] is JsonArray tags)
            {
                if (tags.Count > 64 || tags.Any(item => !IsString(item)))
                {
                    return Fail("model tags rejected", out error);
                }
            }

            if (!OptionalStrings(obj, ["slug", "id", "model", "model_slug", "title", "display_name", "name", "description"], out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsConversationIndex(JsonNode node, out string error)
    {
        if (!ExpectObject(node, ["total", "total_count", "offset", "limit", "next_offset", "nextOffset", "has_more", "hasMore", "next_cursor", "cursor", "nextCursor", "items", "conversations"], 13, out var obj, out error))
        {
            return false;
        }

        foreach (var key in new[] { "total", "total_count", "offset", "limit", "next_offset", "nextOffset" })
        {
            if (obj[key] is not null && !IsNumber(obj[key]))
            {
                return Fail("index numeric field rejected", out error);
            }
        }

        foreach (var key in new[] { "has_more", "hasMore" })
        {
            if (obj[key] is not null && !IsBool(obj[key]))
            {
                return Fail("index boolean field rejected", out error);
            }
        }

        if (!OptionalStrings(obj, ["next_cursor", "cursor", "nextCursor"], out error))
        {
            return false;
        }

        foreach (var key in new[] { "items", "conversations" })
        {
            if (obj[key] is not JsonArray items)
            {
                continue;
            }

            if (items.Count > 500)
            {
                return Fail("conversation index too large", out error);
            }

            foreach (var item in items)
            {
                if (!IsIndexItem(item, out error))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsIndexItem(JsonNode? node, out string error)
    {
        if (!ExpectObject(node, ["id", "conversation_id", "create_time", "createTime", "update_time", "updateTime", "is_archived", "archived", "gizmo_id", "project_id"], 10, out var obj, out error))
        {
            return false;
        }

        foreach (var key in new[] { "create_time", "createTime", "update_time", "updateTime" })
        {
            if (obj[key] is not null && !IsNumber(obj[key]) && !IsString(obj[key]))
            {
                return Fail("timestamp rejected", out error);
            }
        }

        foreach (var key in new[] { "is_archived", "archived" })
        {
            if (obj[key] is not null && !IsBool(obj[key]))
            {
                return Fail("archived flag rejected", out error);
            }
        }

        return OptionalStrings(obj, ["id", "conversation_id", "gizmo_id", "project_id"], out error);
    }

    private static bool IsProjects(JsonNode node, out string error)
    {
        if (!ExpectObject(node, ["has_more", "hasMore", "next_cursor", "cursor", "nextCursor", "gizmos", "items"], 7, out var obj, out error))
        {
            return false;
        }

        foreach (var key in new[] { "has_more", "hasMore" })
        {
            if (obj[key] is not null && !IsBool(obj[key]))
            {
                return Fail("projects boolean field rejected", out error);
            }
        }

        if (!OptionalStrings(obj, ["next_cursor", "cursor", "nextCursor"], out error))
        {
            return false;
        }

        foreach (var key in new[] { "gizmos", "items" })
        {
            if (obj[key] is not JsonArray items)
            {
                continue;
            }

            if (items.Count > 500)
            {
                return Fail("project index too large", out error);
            }

            foreach (var item in items)
            {
                if (!IsProjectItem(item, 0, out error))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsProjectItem(JsonNode? node, int depth, out string error)
    {
        if (depth > 4)
        {
            return Fail("project nesting too deep", out error);
        }

        if (!ExpectObject(node, ["id", "gizmo"], 2, out var obj, out error))
        {
            return false;
        }

        if (!OptionalStrings(obj, ["id"], out error))
        {
            return false;
        }

        return obj["gizmo"] is null || IsProjectItem(obj["gizmo"], depth + 1, out error);
    }

    private static bool IsConversationDetail(JsonNode node, out string error)
    {
        if (!ExpectObject(node, ["conversation_id", "id", "current_node", "update_time", "updateTime", "page_info", "mapping", "messages", "items", "turns"], 10, out var obj, out error))
        {
            return false;
        }

        if (!OptionalStrings(obj, ["conversation_id", "id", "current_node"], out error))
        {
            return false;
        }

        foreach (var key in new[] { "update_time", "updateTime" })
        {
            if (obj[key] is not null && !IsNumber(obj[key]) && !IsString(obj[key]))
            {
                return Fail("timestamp rejected", out error);
            }
        }

        if (obj["page_info"] is not null
            && !ExpectObject(obj["page_info"], ["has_previous_page", "hasPreviousPage", "start_cursor", "startCursor"], 4, out _, out error))
        {
            return false;
        }
        else if (obj["page_info"] is JsonObject pageObj)
        {
            foreach (var key in new[] { "has_previous_page", "hasPreviousPage" })
            {
                if (pageObj[key] is not null && !IsBool(pageObj[key]))
                {
                    return Fail("page_info boolean rejected", out error);
                }
            }

            if (!OptionalStrings(pageObj, ["start_cursor", "startCursor"], out error))
            {
                return false;
            }
        }

        if (obj["mapping"] is JsonObject mapping)
        {
            if (mapping.Count > 8000)
            {
                return Fail("mapping too large", out error);
            }

            foreach (var item in mapping)
            {
                if (!IsMappingNode(item.Value, out error))
                {
                    return false;
                }
            }
        }
        else if (obj["mapping"] is not null)
        {
            return Fail("mapping must be an object", out error);
        }

        foreach (var key in new[] { "messages", "items", "turns" })
        {
            if (obj[key] is not JsonArray array)
            {
                continue;
            }

            if (array.Count > 2000)
            {
                return Fail("conversation collection too large", out error);
            }

            foreach (var item in array)
            {
                if (!IsMappingNode(item, out error))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsMappingNode(JsonNode? node, out string error)
    {
        if (!ExpectObject(node, ["id", "parent", "parent_id", "children", "message", "create_time", "createTime"], 7, out var obj, out error))
        {
            return false;
        }

        if (!OptionalStrings(obj, ["id", "parent", "parent_id"], out error))
        {
            return false;
        }

        foreach (var key in new[] { "create_time", "createTime" })
        {
            if (obj[key] is not null && !IsNumber(obj[key]) && !IsString(obj[key]))
            {
                return Fail("timestamp rejected", out error);
            }
        }

        if (obj["children"] is JsonArray children)
        {
            if (children.Count > 256 || children.Any(item => !IsString(item)))
            {
                return Fail("children rejected", out error);
            }
        }
        else if (obj["children"] is not null)
        {
            return Fail("children must be an array", out error);
        }

        return obj["message"] is null || IsMessage(obj["message"], out error);
    }

    private static bool IsMessage(JsonNode? node, out string error)
    {
        if (!ExpectObject(node, ["id", "create_time", "createTime", "end_turn", "recipient", "author", "content", "metadata"], 8, out var obj, out error))
        {
            return false;
        }

        if (!OptionalStrings(obj, ["id", "recipient"], out error))
        {
            return false;
        }

        foreach (var key in new[] { "create_time", "createTime" })
        {
            if (obj[key] is not null && !IsNumber(obj[key]) && !IsString(obj[key]))
            {
                return Fail("timestamp rejected", out error);
            }
        }

        if (obj["end_turn"] is not null && !IsBool(obj["end_turn"]))
        {
            return Fail("end_turn must be boolean", out error);
        }

        if (obj["author"] is not null
            && !ExpectObject(obj["author"], ["role"], 1, out _, out error, requireStrings: true))
        {
            return false;
        }

        if (obj["content"] is not null
            && !ExpectObject(obj["content"], ["content_type"], 1, out _, out error, requireStrings: true))
        {
            return false;
        }

        return obj["metadata"] is null || IsMetadata(obj["metadata"], out error);
    }

    private static bool IsMetadata(JsonNode? node, out string error)
    {
        if (node is not JsonObject obj)
        {
            return Fail("metadata must be an object", out error);
        }

        if (UnknownKeys(obj, ConversationMetadataKeys, out error))
        {
            return false;
        }

        foreach (var property in obj)
        {
            if (property.Key == "model_experience")
            {
                if (!ExpectObject(property.Value, ["reasoning_effort", "thinking_effort", "effort"], 3, out var experience, out error))
                {
                    return false;
                }

                foreach (var key in new[] { "reasoning_effort", "thinking_effort", "effort" })
                {
                    if (experience[key] is not null && !IsString(experience[key]) && !IsNumber(experience[key]))
                    {
                        return Fail("reasoning effort rejected", out error);
                    }
                }

                continue;
            }

            if (property.Key is "is_visually_hidden_from_conversation")
            {
                if (property.Value is not null && !IsBool(property.Value))
                {
                    return Fail("visually-hidden flag rejected", out error);
                }

                continue;
            }

            if (property.Value is not null && !IsString(property.Value) && !IsNumber(property.Value) && !IsBool(property.Value))
            {
                return Fail("metadata value rejected", out error);
            }
        }

        return true;
    }

    private static bool IsQuota(JsonNode node, out string error)
    {
        if (!ExpectObject(node, ["limits_progress", "model_limits"], 2, out var obj, out error))
        {
            return false;
        }

        foreach (var key in new[] { "limits_progress", "model_limits" })
        {
            if (obj[key] is not JsonArray items)
            {
                continue;
            }

            if (items.Count > 200)
            {
                return Fail("quota list too large", out error);
            }

            foreach (var item in items)
            {
                if (!ExpectObject(item, ["feature_name", "name", "slug", "model", "model_slug", "used", "limit", "resets_at", "reset_at", "resetAt", "resetsAt", "period", "window"], 12, out var entry, out error))
                {
                    return false;
                }

                foreach (var numeric in new[] { "used", "limit" })
                {
                    if (entry[numeric] is not null && !IsNumber(entry[numeric]))
                    {
                        return Fail("quota numeric field rejected", out error);
                    }
                }

                foreach (var stamp in new[] { "resets_at", "reset_at", "resetAt", "resetsAt" })
                {
                    if (entry[stamp] is not null && !IsString(entry[stamp]) && !IsNumber(entry[stamp]))
                    {
                        return Fail("quota reset field rejected", out error);
                    }
                }

                if (!OptionalStrings(entry, ["feature_name", "name", "slug", "model", "model_slug", "period", "window"], out error))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool ExpectObject(
        JsonNode? node,
        IReadOnlyCollection<string> allowed,
        int maxProperties,
        out JsonObject obj,
        out string error,
        bool requireStrings = false)
    {
        obj = null!;
        if (node is not JsonObject value)
        {
            return Fail("expected object", out error);
        }

        if (UnknownKeys(value, allowed, out error))
        {
            return false;
        }

        if (value.Count > maxProperties)
        {
            return Fail("too many fields", out error);
        }

        obj = value;
        return !requireStrings || OptionalStrings(value, allowed, out error);
    }

    private static bool UnknownKeys(JsonObject obj, IReadOnlyCollection<string> allowed, out string error)
    {
        var set = allowed as HashSet<string> ?? new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in obj)
        {
            if (!set.Contains(property.Key))
            {
                error = "unexpected field: " + property.Key;
                return true;
            }
        }

        error = "";
        return false;
    }

    private static bool OptionalStrings(JsonObject obj, IEnumerable<string> keys, out string error)
    {
        foreach (var key in keys)
        {
            if (obj[key] is not null && !IsString(obj[key]))
            {
                return Fail(key + " must be a string", out error);
            }
        }

        error = "";
        return true;
    }

    private static bool IsString(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String;

    private static bool IsBool(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False;

    private static bool IsNumber(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number;

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

        var result = Pick(gizmo, "id");

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
