namespace ProMeter.Providers.ChatGpt;

public sealed class ChatGptProvider : IChatGptProvider
{
    private readonly IChatGptTransport _transport;

    public ChatGptProvider(IChatGptTransport transport)
    {
        _transport = transport;
    }

    public async Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default)
    {
        var session = await GetJsonAsync("GET", ChatGptEndpoints.Session, cancellationToken: cancellationToken);
        var status = AccountParser.ParseSession(session);
        try
        {
            var check = await GetJsonAsync("GET", ChatGptEndpoints.AccountsCheck, cancellationToken: cancellationToken);
            AccountParser.MergeAccountsCheck(status, check);
        }
        catch (ChatGptProviderException)
        {
            try
            {
                var me = await GetJsonAsync("GET", ChatGptEndpoints.Me, cancellationToken: cancellationToken);
                status.Email ??= ChatGptJson.GetString(me, "email");
                status.DisplayName ??= ChatGptJson.GetString(me, "name");
                status.IsSignedIn = status.IsSignedIn || me is not null;
            }
            catch (ChatGptProviderException)
            {
            }
        }

        return status;
    }

    public async Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync("GET", ChatGptEndpoints.Models, cancellationToken: cancellationToken);
        return AccountParser.ParseModels(root);
    }

    public Task<IReadOnlyList<ConversationIndexItem>> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        FetchIndexAsync(archived, minUpdateTime, cancellationToken);

    public Task<IReadOnlyList<ConversationIndexItem>> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        FetchIndexAsync(true, minUpdateTime, cancellationToken);

    public async Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var root = await GetJsonAsync("GET", ChatGptEndpoints.ProjectsSidebarQuery(), cancellationToken: cancellationToken);
        return AccountParser.ParseProjects(root);
    }

    public async Task<IReadOnlyList<ConversationIndexItem>> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default)
    {
        var items = new List<ConversationIndexItem>();
        string? cursor = "0";
        var pages = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && pages < 50)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ProjectConversationsById(projectId, cursor), cancellationToken: cancellationToken);
            var page = AccountParser.ParseConversationIndex(root, archived: false, projectId, "project");
            var stop = false;
            foreach (var item in page)
            {
                if (minUpdateTime is double min && item.UpdateTime > 0 && item.UpdateTime < min)
                {
                    stop = true;
                    break;
                }

                items.Add(item);
            }

            if (stop || page.Count == 0 || ChatGptJson.GetBool(root, "has_more") != true)
            {
                break;
            }

            cursor = ChatGptJson.GetString(root, "next_cursor", "cursor");
            pages++;
        }

        return items;
    }

    public async Task<JsonNode?> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
        await GetJsonAsync("GET", ChatGptEndpoints.ConversationById(conversationId), cancellationToken: cancellationToken);

    public async Task<QuotaMetadata> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var init = await GetJsonAsync(
                "POST",
                ChatGptEndpoints.ConversationInit,
                """{"conversation_mode_kind":"primary_assistant"}""",
                cancellationToken);
            var parsed = AccountParser.ParseQuotaMetadata(init);
            if (parsed.Found)
            {
                return parsed;
            }
        }
        catch (ChatGptProviderException)
        {
        }

        try
        {
            var models = await GetJsonAsync("GET", ChatGptEndpoints.Models, cancellationToken: cancellationToken);
            var parsed = AccountParser.ParseQuotaMetadata(models);
            if (parsed.Found)
            {
                return parsed;
            }
        }
        catch (ChatGptProviderException)
        {
        }

        return new QuotaMetadata();
    }

    private async Task<IReadOnlyList<ConversationIndexItem>> FetchIndexAsync(bool archived, double? minUpdateTime, CancellationToken cancellationToken)
    {
        var items = new List<ConversationIndexItem>();
        var offset = 0;
        const int limit = 100;
        for (var page = 0; page < 40; page++)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ConversationsPage(offset, limit, archived), cancellationToken: cancellationToken);
            var pageItems = AccountParser.ParseConversationIndex(root, archived, source: archived ? "archived" : "chat");
            if (pageItems.Count == 0)
            {
                break;
            }

            var stop = false;
            foreach (var item in pageItems)
            {
                if (minUpdateTime is double min && item.UpdateTime > 0 && item.UpdateTime < min)
                {
                    stop = true;
                    break;
                }

                items.Add(item);
            }

            if (stop || pageItems.Count < limit)
            {
                break;
            }

            offset += limit;
        }

        return items;
    }

    private async Task<JsonNode?> GetJsonAsync(string method, string path, string? body = null, CancellationToken cancellationToken = default)
    {
        var response = await _transport.SendAsync(method, path, body, cancellationToken);
        if (response.IsUnauthorized)
        {
            throw new ChatGptProviderException("Authentication required.", response.Status, response.RetryAfter);
        }

        if (response.IsRateLimited)
        {
            throw new ChatGptProviderException("Rate limited.", response.Status, response.RetryAfter);
        }

        if (response.IsServerError)
        {
            throw new ChatGptProviderException("ChatGPT server error.", response.Status, response.RetryAfter);
        }

        if (!response.IsSuccess)
        {
            throw new ChatGptProviderException(
                string.IsNullOrWhiteSpace(response.Error) ? $"HTTP {response.Status}" : response.Error,
                response.Status,
                response.RetryAfter);
        }

        if (string.IsNullOrWhiteSpace(response.Body))
        {
            return null;
        }

        var node = ChatGptJson.ParseNode(response.Body);
        if (node is null)
        {
            throw new ChatGptProviderException("Provider schema mismatch", response.Status, schemaMismatch: true);
        }

        return node;
    }
}
