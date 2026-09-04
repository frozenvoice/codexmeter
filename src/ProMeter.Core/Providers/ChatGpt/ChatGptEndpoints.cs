namespace ProMeter.Providers.ChatGpt;

/// <summary>
/// Single place for ChatGPT web origin and internal paths observed in 2026.
/// Business logic and UI must not hard-code these strings.
/// </summary>
public static class ChatGptEndpoints
{
    public const string Origin = "https://chatgpt.com";
    public const string LoginUrl = "https://chatgpt.com/auth/login";
    public const string HomeUrl = "https://chatgpt.com/";

    public const string Session = "/api/auth/session";
    public const string Me = "/backend-api/me";
    public const string AccountsCheck = "/backend-api/accounts/check/v4-2023-04-27";
    public const string Models = "/backend-api/models";
    public const string Conversations = "/backend-api/conversations";
    public const string Conversation = "/backend-api/conversation/{id}";
    public const string ConversationInit = "/backend-api/conversation/init";
    public const string ProjectsSidebar = "/backend-api/gizmos/snorlax/sidebar";
    public const string ProjectConversations = "/backend-api/gizmos/{id}/conversations";

    public static string ConversationById(string id) =>
        Conversation.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);

    public static string ProjectConversationsById(string id, string? cursor = null)
    {
        var path = ProjectConversations.Replace("{id}", Uri.EscapeDataString(id), StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(cursor)
            ? path + "?cursor=0"
            : path + "?cursor=" + Uri.EscapeDataString(cursor);
    }

    public static string ConversationsPage(int offset, int limit, bool archived) =>
        $"{Conversations}?offset={offset}&limit={limit}&order=updated&is_archived={archived.ToString().ToLowerInvariant()}";

    public static string ProjectsSidebarQuery() =>
        $"{ProjectsSidebar}?conversations_per_gizmo=0&owned_only=true";
}
