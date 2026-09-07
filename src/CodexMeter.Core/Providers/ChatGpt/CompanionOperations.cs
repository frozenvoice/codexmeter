namespace CodexMeter.Providers.ChatGpt;

public enum CompanionOperation
{
    GetSessionStatus,
    GetAccountCheck,
    GetAccountMe,
    GetModels,
    GetConversationIndex,
    GetArchivedConversationIndex,
    GetProjects,
    GetProjectConversations,
    GetConversationHead,
    GetConversationFull,
    GetConversationLegacy,
    GetOlderConversationMessages,
    GetQuotaInit
}

public static class CompanionOperationPolicy
{
    public static bool IsReconnectRetrySafe(CompanionOperation operation) => operation switch
    {
        CompanionOperation.GetSessionStatus => true,
        CompanionOperation.GetAccountCheck => true,
        CompanionOperation.GetAccountMe => true,
        CompanionOperation.GetModels => true,
        CompanionOperation.GetConversationIndex => true,
        CompanionOperation.GetArchivedConversationIndex => true,
        CompanionOperation.GetProjects => true,
        CompanionOperation.GetProjectConversations => true,
        CompanionOperation.GetConversationHead => true,
        CompanionOperation.GetConversationFull => true,
        CompanionOperation.GetConversationLegacy => true,
        CompanionOperation.GetOlderConversationMessages => true,
        CompanionOperation.GetQuotaInit => true,
        _ => false
    };
}

public sealed class CompanionOperationArgs
{
    public string? ConversationId { get; init; }
    public string? ProjectId { get; init; }
    public string? Cursor { get; init; }
    public int? Offset { get; init; }
    public int? Limit { get; init; }
    public bool? Archived { get; init; }
    public string? Body { get; init; }
}

public static class CompanionOperationRouter
{
    public const int MaxIdLength = 128;
    public const int MaxCursorLength = 512;
    public const int MaxBodyLength = 4096;
    public const string QuotaInitBody = """{"conversation_mode_kind":"primary_assistant"}""";

    public static bool TryMap(string? method, string? path, string? jsonBody, out CompanionOperation operation, out CompanionOperationArgs args, out string error)
    {
        operation = default;
        args = new CompanionOperationArgs();
        error = "";
        if (!CanonicalTargetPolicy.TryValidate(path, out var canonical, out error))
        {
            return false;
        }

        var http = method?.Trim().ToUpperInvariant() ?? "";
        var cut = canonical.IndexOf('?', StringComparison.Ordinal);
        var pathname = cut >= 0 ? canonical[..cut] : canonical;
        var query = cut >= 0 ? ParseQuery(canonical[(cut + 1)..]) : new Dictionary<string, string>(StringComparer.Ordinal);
        if (http is "PUT" or "PATCH" or "DELETE")
        {
            error = "write methods are forbidden";
            return false;
        }

        if (http == "POST")
        {
            if (!string.Equals(pathname, ChatGptEndpoints.ConversationInit, StringComparison.OrdinalIgnoreCase))
            {
                error = "arbitrary POST is forbidden";
                return false;
            }

            if (!IsApprovedQuotaInitBody(jsonBody))
            {
                error = "quota-init body rejected";
                return false;
            }

            operation = CompanionOperation.GetQuotaInit;
            args = new CompanionOperationArgs { Body = QuotaInitBody };
            return true;
        }

        if (http != "GET")
        {
            error = "unsupported HTTP method";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(jsonBody))
        {
            error = "GET must not include a body";
            return false;
        }

        if (string.Equals(pathname, ChatGptEndpoints.Session, StringComparison.OrdinalIgnoreCase) && query.Count == 0)
        {
            operation = CompanionOperation.GetSessionStatus;
            return true;
        }

        if (string.Equals(pathname, ChatGptEndpoints.Me, StringComparison.OrdinalIgnoreCase) && query.Count == 0)
        {
            operation = CompanionOperation.GetAccountMe;
            return true;
        }

        if (string.Equals(pathname, ChatGptEndpoints.AccountsCheck, StringComparison.OrdinalIgnoreCase) && query.Count == 0)
        {
            operation = CompanionOperation.GetAccountCheck;
            return true;
        }

        if (string.Equals(pathname, ChatGptEndpoints.Models, StringComparison.OrdinalIgnoreCase) && query.Count == 0)
        {
            operation = CompanionOperation.GetModels;
            return true;
        }

        if (string.Equals(pathname, ChatGptEndpoints.Conversations, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryIndexQuery(query, out var offset, out var limit, out var archived, out error))
            {
                return false;
            }

            operation = archived ? CompanionOperation.GetArchivedConversationIndex : CompanionOperation.GetConversationIndex;
            args = new CompanionOperationArgs { Offset = offset, Limit = limit, Archived = archived };
            return true;
        }

        if (string.Equals(pathname, ChatGptEndpoints.ProjectsSidebar, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryProjectsQuery(query, out var cursor, out error))
            {
                return false;
            }

            operation = CompanionOperation.GetProjects;
            args = new CompanionOperationArgs { Cursor = cursor };
            return true;
        }

        if (TryMatch(pathname, "/backend-api/gizmos/", "/conversations", out var projectId))
        {
            if (!IsSafeId(projectId, out error) || !TryProjectConversationsQuery(query, out var cursor, out error))
            {
                return false;
            }

            operation = CompanionOperation.GetProjectConversations;
            args = new CompanionOperationArgs { ProjectId = projectId, Cursor = cursor };
            return true;
        }

        if (TryMatch(pathname, "/backend-api/conversations/", "/messages", out var olderId))
        {
            if (!IsSafeId(olderId, out error) || !TryOlderQuery(query, out var cursor, out error))
            {
                return false;
            }

            operation = CompanionOperation.GetOlderConversationMessages;
            args = new CompanionOperationArgs { ConversationId = olderId, Cursor = cursor };
            return true;
        }

        if (TryMatch(pathname, "/backend-api/conversations/", "", out var headId) && headId.Length > 0)
        {
            if (!IsSafeId(headId, out error) || !TryHeadQuery(query, out error))
            {
                return false;
            }

            operation = CompanionOperation.GetConversationHead;
            args = new CompanionOperationArgs { ConversationId = headId };
            return true;
        }

        if (TryMatch(pathname, "/backend-api/conversation/", "", out var convId) && convId.Length > 0)
        {
            if (!IsSafeId(convId, out error))
            {
                return false;
            }

            if (query.Count == 0)
            {
                operation = CompanionOperation.GetConversationLegacy;
                args = new CompanionOperationArgs { ConversationId = convId };
                return true;
            }

            if (query.Count == 1
                && query.TryGetValue("include_full_conversation", out var full)
                && string.Equals(full, "true", StringComparison.OrdinalIgnoreCase))
            {
                operation = CompanionOperation.GetConversationFull;
                args = new CompanionOperationArgs { ConversationId = convId };
                return true;
            }

            error = "unapproved conversation query";
            return false;
        }

        error = "unknown operation";
        return false;
    }

    public static bool TryBuild(CompanionOperation operation, CompanionOperationArgs? args, out string method, out string path, out string? body, out string error)
    {
        method = "GET";
        path = "";
        body = null;
        error = "";
        args ??= new CompanionOperationArgs();
        switch (operation)
        {
            case CompanionOperation.GetSessionStatus:
                path = ChatGptEndpoints.Session;
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetAccountMe:
                path = ChatGptEndpoints.Me;
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetAccountCheck:
                path = ChatGptEndpoints.AccountsCheck;
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetModels:
                path = ChatGptEndpoints.Models;
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetConversationIndex:
            case CompanionOperation.GetArchivedConversationIndex:
                if (!IsSafeOffsetLimit(args.Offset ?? 0, args.Limit ?? ConversationIndexPager.RequestedLimit, out error))
                {
                    return false;
                }

                path = ChatGptEndpoints.ConversationsPage(
                    args.Offset ?? 0,
                    args.Limit ?? ConversationIndexPager.RequestedLimit,
                    operation == CompanionOperation.GetArchivedConversationIndex || args.Archived == true);
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetProjects:
                if (!IsSafeCursor(args.Cursor, optional: true, out error))
                {
                    return false;
                }

                path = ChatGptEndpoints.ProjectsSidebarQuery(args.Cursor);
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetProjectConversations:
                if (!IsSafeId(args.ProjectId, out error) || !IsSafeCursor(args.Cursor, optional: true, out error))
                {
                    return false;
                }

                path = ChatGptEndpoints.ProjectConversationsById(args.ProjectId!, args.Cursor);
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetConversationHead:
                if (!IsSafeId(args.ConversationId, out error))
                {
                    return false;
                }

                path = ChatGptEndpoints.ConversationTurns(args.ConversationId!);
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetConversationFull:
                if (!IsSafeId(args.ConversationId, out error))
                {
                    return false;
                }

                path = ChatGptEndpoints.ConversationFull(args.ConversationId!);
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetConversationLegacy:
                if (!IsSafeId(args.ConversationId, out error))
                {
                    return false;
                }

                path = ChatGptEndpoints.ConversationById(args.ConversationId!);
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetOlderConversationMessages:
                if (!IsSafeId(args.ConversationId, out error) || !IsSafeCursor(args.Cursor, optional: false, out error))
                {
                    return false;
                }

                path = ChatGptEndpoints.ConversationOlderMessages(args.ConversationId!, args.Cursor!);
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            case CompanionOperation.GetQuotaInit:
                method = "POST";
                path = ChatGptEndpoints.ConversationInit;
                body = QuotaInitBody;
                return CanonicalTargetPolicy.TryValidate(path, out path, out error);
            default:
                error = "unknown operation";
                return false;
        }
    }

    public static bool IsSafeId(string? value, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdLength)
        {
            error = "invalid identifier";
            return false;
        }

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                continue;
            }

            error = "invalid identifier";
            return false;
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            error = "invalid identifier";
            return false;
        }

        return true;
    }

    public static bool IsApprovedQuotaInitBody(string? jsonBody)
    {
        if (string.IsNullOrWhiteSpace(jsonBody) || jsonBody.Length > MaxBodyLength)
        {
            return false;
        }

        var node = ChatGptJson.ParseNode(jsonBody) as JsonObject;
        if (node is null || node.Count != 1)
        {
            return false;
        }

        return string.Equals(
            ChatGptJson.GetString(node, "conversation_mode_kind"),
            "primary_assistant",
            StringComparison.Ordinal);
    }

    private static bool TryIndexQuery(IReadOnlyDictionary<string, string> query, out int offset, out int limit, out bool archived, out string error)
    {
        offset = 0;
        limit = ConversationIndexPager.RequestedLimit;
        archived = false;
        error = "";
        if (!query.TryGetValue("offset", out var offsetText)
            || !query.TryGetValue("limit", out var limitText)
            || !query.TryGetValue("order", out var order)
            || !query.TryGetValue("is_archived", out var archivedText)
            || query.Count != 4
            || !string.Equals(order, "updated", StringComparison.Ordinal))
        {
            error = "unapproved index query";
            return false;
        }

        if (!int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out offset)
            || !int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || !IsSafeOffsetLimit(offset, limit, out error))
        {
            return false;
        }

        if (string.Equals(archivedText, "true", StringComparison.OrdinalIgnoreCase))
        {
            archived = true;
            return true;
        }

        if (string.Equals(archivedText, "false", StringComparison.OrdinalIgnoreCase))
        {
            archived = false;
            return true;
        }

        error = "unapproved index query";
        return false;
    }

    private static bool TryProjectsQuery(IReadOnlyDictionary<string, string> query, out string? cursor, out string error)
    {
        cursor = null;
        error = "";
        if (!query.TryGetValue("conversations_per_gizmo", out var per)
            || !query.TryGetValue("owned_only", out var owned)
            || !string.Equals(per, "0", StringComparison.Ordinal)
            || !string.Equals(owned, "true", StringComparison.Ordinal)
            || query.Count > 3)
        {
            error = "unapproved projects query";
            return false;
        }

        if (query.Count == 3)
        {
            if (!query.TryGetValue("cursor", out cursor) || !IsSafeCursor(cursor, optional: false, out error))
            {
                return false;
            }
        }

        return query.Count is 2 or 3;
    }

    private static bool TryProjectConversationsQuery(IReadOnlyDictionary<string, string> query, out string? cursor, out string error)
    {
        cursor = null;
        error = "";
        if (query.Count != 1 || !query.TryGetValue("cursor", out cursor))
        {
            error = "unapproved project conversations query";
            return false;
        }

        return IsSafeCursor(cursor, optional: false, out error);
    }

    private static bool TryHeadQuery(IReadOnlyDictionary<string, string> query, out string error)
    {
        error = "";
        if (query.Count == 2
            && query.TryGetValue("include_has_versions", out var versions)
            && query.TryGetValue("num_turns", out var turns)
            && string.Equals(versions, "true", StringComparison.OrdinalIgnoreCase)
            && string.Equals(turns, "100", StringComparison.Ordinal))
        {
            return true;
        }

        error = "unapproved conversation head query";
        return false;
    }

    private static bool TryOlderQuery(IReadOnlyDictionary<string, string> query, out string cursor, out string error)
    {
        cursor = "";
        error = "";
        if (query.Count == 3
            && query.TryGetValue("before", out var before)
            && query.TryGetValue("include_has_versions", out var versions)
            && query.TryGetValue("num_turns", out var turns)
            && string.Equals(versions, "true", StringComparison.OrdinalIgnoreCase)
            && string.Equals(turns, "100", StringComparison.Ordinal))
        {
            cursor = before ?? "";
            return IsSafeCursor(cursor, optional: false, out error);
        }

        error = "unapproved older-messages query";
        return false;
    }

    private static bool IsSafeOffsetLimit(int offset, int limit, out string error)
    {
        error = "";
        if (offset is < 0 or > 100_000 || limit is < 1 or > 100)
        {
            error = "offset or limit out of range";
            return false;
        }

        return true;
    }

    private static bool IsSafeCursor(string? cursor, bool optional, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(cursor))
        {
            if (optional)
            {
                return true;
            }

            error = "cursor required";
            return false;
        }

        if (cursor.Length > MaxCursorLength || OriginPolicy.ContainsControl(cursor) || cursor.Contains('\\'))
        {
            error = "invalid cursor";
            return false;
        }

        return true;
    }

    private static bool TryMatch(string pathname, string prefix, string suffix, out string id)
    {
        id = "";
        if (!pathname.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = pathname[prefix.Length..];
        if (suffix.Length == 0)
        {
            id = rest;
            return id.Length > 0 && !id.Contains('/');
        }

        if (!rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        id = rest[..^suffix.Length];
        return id.Length > 0 && !id.Contains('/');
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part[..eq]);
            var value = Uri.UnescapeDataString(part[(eq + 1)..]);
            map[key] = value;
        }

        return map;
    }
}
