namespace CycleArc.Providers.ChatGpt;

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
        if (session["signedIn"] is JsonValue signedValue && signedValue.TryGetValue(out bool signedIn))
        {
            status.IsSignedIn = signedIn;
        }
        else
        {
            status.IsSignedIn = !string.IsNullOrWhiteSpace(status.Email)
                || !string.IsNullOrWhiteSpace(status.UserId)
                || session["accessToken"] is not null;
        }
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

    public static IndexParseResult ParseConversationIndex(JsonNode? root, bool archived, string? projectId = null, string source = "chat")
    {
        if (root is null)
        {
            return new IndexParseResult { SchemaMismatch = true, TimestampComplete = false };
        }

        var array = ConversationIndexArray(root);
        if (array is null)
        {
            return new IndexParseResult { TimestampComplete = false };
        }

        var items = new List<ConversationIndexItem>();
        var missingTimestamps = 0;
        var missingIds = 0;
        var malformed = 0;
        var raw = 0;
        foreach (var item in ChatGptJson.Enumerate(array))
        {
            raw++;
            if (item is not JsonObject)
            {
                missingIds++;
                malformed++;
                continue;
            }

            var id = ChatGptJson.GetString(item, "id", "conversation_id");
            if (string.IsNullOrWhiteSpace(id))
            {
                missingIds++;
                malformed++;
                continue;
            }

            var updateTime = TimestampParser.ToUnixSeconds(item, "update_time", "updateTime");
            if (updateTime <= 0)
            {
                missingTimestamps++;
            }

            items.Add(new ConversationIndexItem
            {
                Id = id,
                Title = ChatGptJson.GetString(item, "title"),
                CreateTime = TimestampParser.ToUnixSeconds(item, "create_time", "createTime"),
                UpdateTime = updateTime,
                Archived = archived || ChatGptJson.GetBool(item, "is_archived", "archived") == true,
                ProjectId = projectId ?? ChatGptJson.GetString(item, "gizmo_id", "project_id"),
                Source = source
            });
        }

        var emptyValid = items.Count == 0;
        return new IndexParseResult
        {
            Items = items,
            RecognizedShape = true,
            SchemaMismatch = raw > 0 && emptyValid,
            Incomplete = items.Count > 0 && malformed > 0,
            TimestampComplete = missingTimestamps == 0,
            MissingTimestamps = missingTimestamps,
            TotalRawItems = raw,
            ValidItems = items.Count,
            MalformedItems = malformed,
            MissingIds = missingIds
        };
    }

    public static JsonArray? ConversationIndexArray(JsonNode? root) =>
        IndexCollections.Find(root, IndexCollections.Conversation);

    public static ProjectParseResult ParseProjects(JsonNode? root)
    {
        if (root is null)
        {
            return new ProjectParseResult { SchemaMismatch = true };
        }

        var items = ProjectIndexArray(root);
        if (items is null)
        {
            return new ProjectParseResult();
        }

        var projects = new List<ProjectInfo>();
        var raw = 0;
        var missingIds = 0;
        var malformed = 0;
        foreach (var item in ChatGptJson.Enumerate(items))
        {
            raw++;
            if (item is not JsonObject)
            {
                missingIds++;
                malformed++;
                continue;
            }

            var gizmo = item["gizmo"]?["gizmo"] as JsonObject
                ?? item["gizmo"] as JsonObject
                ?? item as JsonObject;
            var id = ChatGptJson.GetString(gizmo, "id") ?? ChatGptJson.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                missingIds++;
                malformed++;
                continue;
            }

            var display = gizmo?["display"] as JsonObject;
            projects.Add(new ProjectInfo
            {
                Id = id,
                Name = ChatGptJson.GetString(display, "name") ?? ChatGptJson.GetString(gizmo, "name", "title")
            });
        }

        return new ProjectParseResult
        {
            Projects = projects,
            RecognizedShape = true,
            SchemaMismatch = raw > 0 && projects.Count == 0,
            Incomplete = projects.Count > 0 && malformed > 0,
            TotalRawItems = raw,
            ValidItems = projects.Count,
            MalformedItems = malformed,
            MissingIds = missingIds
        };
    }

    public static JsonArray? ProjectIndexArray(JsonNode? root) =>
        IndexCollections.Find(root, IndexCollections.Project);

    public static QuotaMetadataSet ParseQuotaMetadata(JsonNode? root)
    {
        var set = new QuotaMetadataSet { RawSummary = SummarizeWithoutBodies(root) };
        if (root is null)
        {
            return set;
        }

        foreach (var item in ChatGptJson.Enumerate(root["limits_progress"]))
        {
            var feature = ChatGptJson.GetString(item, "feature_name", "name") ?? "";
            ApplyQuotaEntry(set, feature, item);
        }

        foreach (var item in ChatGptJson.Enumerate(root["model_limits"]))
        {
            var slug = ChatGptJson.GetString(item, "slug", "model", "model_slug", "name") ?? "";
            ApplyQuotaEntry(set, slug, item);
        }

        set.ProServerStatus = ProServerStatusParser.Parse(root);
        return set;
    }

    public static bool IsGptProAllowanceFeature(string feature) =>
        ClassifyGptProWindow(feature) != QuotaWindowKind.Unclassified
        || LooksLikeGptProDiagnostic(feature);

    public static QuotaWindowKind ClassifyGptProWindow(string feature)
    {
        var key = NormalizeFeature(feature);
        if (string.IsNullOrWhiteSpace(key) || IsExcludedFeature(key))
        {
            return QuotaWindowKind.Unclassified;
        }

        var daily = key.Contains("daily");
        var weekly = key.Contains("weekly");
        var combined = key.Contains("combined");
        var gpt6 = key.Contains("gpt6");
        var sol56 = key.Contains("56") || (key.Contains("sol") && key.Contains("pro"));
        var hasPro = key.Contains("pro");
        if (!hasPro)
        {
            return QuotaWindowKind.Unclassified;
        }

        if (combined)
        {
            return daily && !weekly ? QuotaWindowKind.CombinedProDaily : QuotaWindowKind.Unclassified;
        }

        if (gpt6)
        {
            return weekly && !daily ? QuotaWindowKind.Gpt6ProWeekly : QuotaWindowKind.Unclassified;
        }

        if (sol56)
        {
            return daily && !weekly ? QuotaWindowKind.SolProDaily : QuotaWindowKind.Unclassified;
        }

        if (weekly && !daily)
        {
            return QuotaWindowKind.SharedProWeekly;
        }

        return QuotaWindowKind.Unclassified;
    }

    private static bool LooksLikeGptProDiagnostic(string feature)
    {
        var key = NormalizeFeature(feature);
        if (string.IsNullOrWhiteSpace(key) || IsExcludedFeature(key) || !key.Contains("pro"))
        {
            return false;
        }

        return key.Contains("gpt6") || key.Contains("56") || key.Contains("sol") || key.Contains("weekly") || key.Contains("combined");
    }

    private static bool IsExcludedFeature(string key) =>
        key.Contains("codex") || key.Contains("deepresearch") || key.Contains("voice")
        || key.Contains("image") || key.Contains("plus") || key.Contains("goplan");

    private static void ApplyQuotaEntry(QuotaMetadataSet set, string feature, JsonNode item)
    {
        if (!IsGptProAllowanceFeature(feature))
        {
            return;
        }

        var window = ReadWindow(feature, item);
        var kind = ClassifyGptProWindow(feature);
        switch (kind)
        {
            case QuotaWindowKind.SharedProWeekly:
                set.SharedProWeekly ??= window;
                break;
            case QuotaWindowKind.Gpt6ProWeekly:
                set.Gpt6ProWeekly ??= window;
                break;
            case QuotaWindowKind.SolProDaily:
                set.SolProDaily ??= window;
                break;
            case QuotaWindowKind.CombinedProDaily:
                set.CombinedProDaily ??= window;
                break;
            default:
                set.Diagnostics.Add(window);
                break;
        }
    }

    private static QuotaWindow ReadWindow(string feature, JsonNode item)
    {
        var window = new QuotaWindow { Found = true, FeatureName = feature };
        window.Used = (int?)ChatGptJson.GetDouble(item, "used");
        var remaining = ChatGptJson.GetDouble(item, "remaining");
        var limit = ChatGptJson.GetDouble(item, "limit");
        if (limit is not null)
        {
            window.Limit = (int)limit.Value;
        }
        else if (remaining is not null && window.Used is not null)
        {
            window.Limit = window.Used + (int)remaining.Value;
        }

        window.ResetAt = TimestampParser.ToDateTimeOffset(
            item,
            "reset_after",
            "resets_after",
            "reset_at",
            "resetAt",
            "resets_at",
            "resetsAt");
        return window;
    }

    private static string NormalizeFeature(string feature) =>
        new string(feature.Trim().ToLowerInvariant().Where(ch => char.IsLetterOrDigit(ch)).ToArray());

    private static string? SummarizeWithoutBodies(JsonNode? root)
    {
        if (root is null)
        {
            return null;
        }

        var clone = root.DeepClone();
        ConversationDetailLoader.StripBodies(clone);
        var text = clone.ToJsonString();
        return text.Length > 2000 ? text[..2000] : text;
    }
}
