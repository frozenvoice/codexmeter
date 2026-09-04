namespace ProMeter.Providers.ChatGpt;

public static class AccountParser
{
    public static AccountStatus ParseSession(JsonNode? session)
    {
        var status = new AccountStatus { ObservedAt = DateTimeOffset.UtcNow };
        if (session is null)
        {
            return status;
        }

        var user = session["user"] as JsonObject ?? session;
        status.Email = ChatGptJson.GetString(user, "email");
        status.DisplayName = ChatGptJson.GetString(user, "name");
        status.UserId = ChatGptJson.GetString(user, "id");
        status.IsSignedIn = !string.IsNullOrWhiteSpace(status.Email)
            || !string.IsNullOrWhiteSpace(status.UserId)
            || session["accessToken"] is not null;
        return status;
    }

    public static void MergeAccountsCheck(AccountStatus status, JsonNode? check)
    {
        if (check is null)
        {
            return;
        }

        var accounts = check["accounts"] as JsonObject;
        JsonNode? account = null;
        if (accounts is not null)
        {
            account = accounts["default"] ?? accounts.FirstOrDefault().Value;
        }

        account ??= check;
        var entitlement = account?["entitlement"] as JsonObject;
        var accountNode = account?["account"] as JsonObject;
        status.PlanType = ChatGptJson.GetString(entitlement, "subscription_plan", "plan_type")
            ?? ChatGptJson.GetString(accountNode, "plan_type", "planType");
        status.HasActiveSubscription = ChatGptJson.GetBool(entitlement, "has_active_subscription") == true
            || !string.IsNullOrWhiteSpace(status.PlanType);
        status.IsSignedIn = status.IsSignedIn || account is not null;
    }

    public static IReadOnlyList<ModelCatalogEntry> ParseModels(JsonNode? root)
    {
        var list = new List<ModelCatalogEntry>();
        if (root is null)
        {
            return list;
        }

        var now = DateTimeOffset.UtcNow;
        var models = root["models"] as JsonArray ?? root as JsonArray;
        if (models is null && root["categories"] is JsonArray categories)
        {
            foreach (var category in ChatGptJson.Enumerate(categories))
            {
                CollectModels(list, category["models"] as JsonArray, now);
            }
        }
        else
        {
            CollectModels(list, models, now);
        }

        return list;
    }

    private static void CollectModels(List<ModelCatalogEntry> list, JsonArray? models, DateTimeOffset now)
    {
        if (models is null)
        {
            return;
        }

        foreach (var model in ChatGptJson.Enumerate(models))
        {
            var slug = ChatGptJson.GetString(model, "slug", "id", "model", "model_slug");
            if (string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            list.Add(new ModelCatalogEntry
            {
                Slug = slug,
                Title = ChatGptJson.GetString(model, "title", "display_name", "name"),
                Description = ChatGptJson.GetString(model, "description"),
                Tags = model["tags"]?.ToJsonString(),
                ObservedAt = now
            });
        }
    }

    public static IReadOnlyList<ConversationIndexItem> ParseConversationIndex(JsonNode? root, bool archived, string? projectId = null, string source = "chat")
    {
        var items = new List<ConversationIndexItem>();
        if (root is null)
        {
            return items;
        }

        var array = root["items"] as JsonArray ?? root["conversations"] as JsonArray ?? root as JsonArray;
        if (array is null)
        {
            return items;
        }

        foreach (var item in ChatGptJson.Enumerate(array))
        {
            var id = ChatGptJson.GetString(item, "id", "conversation_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            items.Add(new ConversationIndexItem
            {
                Id = id,
                Title = ChatGptJson.GetString(item, "title"),
                CreateTime = ChatGptJson.GetDouble(item, "create_time", "createTime") ?? 0,
                UpdateTime = ChatGptJson.GetDouble(item, "update_time", "updateTime") ?? 0,
                Archived = archived || ChatGptJson.GetBool(item, "is_archived", "archived") == true,
                ProjectId = projectId ?? ChatGptJson.GetString(item, "gizmo_id", "project_id"),
                Source = source
            });
        }

        return items;
    }

    public static IReadOnlyList<ProjectInfo> ParseProjects(JsonNode? root)
    {
        var projects = new List<ProjectInfo>();
        if (root is null)
        {
            return projects;
        }

        var items = root["items"] as JsonArray ?? root["gizmos"] as JsonArray ?? root as JsonArray;
        if (items is null)
        {
            return projects;
        }

        foreach (var item in ChatGptJson.Enumerate(items))
        {
            var gizmo = item["gizmo"]?["gizmo"] as JsonObject
                ?? item["gizmo"] as JsonObject
                ?? item as JsonObject;
            var id = ChatGptJson.GetString(gizmo, "id") ?? ChatGptJson.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var display = gizmo?["display"] as JsonObject;
            projects.Add(new ProjectInfo
            {
                Id = id,
                Name = ChatGptJson.GetString(display, "name") ?? ChatGptJson.GetString(gizmo, "name", "title")
            });
        }

        return projects;
    }

    public static QuotaMetadata ParseQuotaMetadata(JsonNode? root)
    {
        var metadata = new QuotaMetadata { RawSummary = root?.ToJsonString() };
        if (root is null)
        {
            return metadata;
        }

        foreach (var item in ChatGptJson.Enumerate(root["limits_progress"]))
        {
            var feature = ChatGptJson.GetString(item, "feature_name", "name") ?? "";
            if (!LooksLikeProLimit(feature))
            {
                continue;
            }

            metadata.Found = true;
            metadata.FeatureName = feature;
            metadata.Used = (int?)ChatGptJson.GetDouble(item, "used");
            var remaining = ChatGptJson.GetDouble(item, "remaining");
            var limit = ChatGptJson.GetDouble(item, "limit");
            if (limit is not null)
            {
                metadata.Limit = (int)limit.Value;
            }
            else if (remaining is not null && metadata.Used is not null)
            {
                metadata.Limit = metadata.Used + (int)remaining.Value;
            }

            metadata.ResetAt = ParseReset(item);
            metadata.IsAuthoritative = metadata.ResetAt is not null || metadata.Limit is not null;
        }

        foreach (var item in ChatGptJson.Enumerate(root["model_limits"]))
        {
            if (metadata.ResetAt is null)
            {
                metadata.ResetAt = ParseReset(item);
                if (metadata.ResetAt is not null)
                {
                    metadata.Found = true;
                    metadata.IsAuthoritative = true;
                }
            }
        }

        metadata.ResetAt ??= ParseReset(root);
        if (metadata.ResetAt is not null)
        {
            metadata.Found = true;
            metadata.IsAuthoritative = true;
        }

        return metadata;
    }

    private static bool LooksLikeProLimit(string feature)
    {
        var key = feature.ToLowerInvariant();
        return key.Contains("pro") || key.Contains("gpt-6") || key.Contains("sol");
    }

    private static DateTimeOffset? ParseReset(JsonNode? node)
    {
        var resetAfter = ChatGptJson.GetString(node, "reset_after", "resets_after", "reset_at", "resetAt");
        if (DateTimeOffset.TryParse(resetAfter, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        var unix = ChatGptJson.GetDouble(node, "reset_at", "resets_at", "resetAfterSeconds");
        if (unix is > 1_000_000_000)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)unix.Value);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }
}
