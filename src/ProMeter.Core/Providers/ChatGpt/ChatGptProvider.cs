namespace ProMeter.Providers.ChatGpt;

public sealed class ChatGptProvider : IChatGptProvider
{
    private readonly IChatGptTransport _transport;
    private readonly ConversationDetailLoader _loader = new();

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

    public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        FetchIndexAsync(archived, minUpdateTime, cancellationToken);

    public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        FetchIndexAsync(true, minUpdateTime, cancellationToken);

    public async Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var projects = new List<ProjectInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        var pages = 0;
        var incomplete = false;
        while (pages < ConversationIndexPager.MaxPages)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ProjectsSidebarQuery(cursor), cancellationToken: cancellationToken);
            var page = AccountParser.ParseProjects(root);
            projects.AddRange(page);
            pages++;
            var info = ConversationIndexPager.ReadCursor(root);
            if (!ConversationIndexPager.ShouldFetchNextCursor(cursor, info, seen))
            {
                if (info.HasMore == true && (string.IsNullOrWhiteSpace(info.NextCursor) || info.NextCursor == cursor))
                {
                    incomplete = true;
                }

                break;
            }

            cursor = info.NextCursor;
        }

        if (pages >= ConversationIndexPager.MaxPages)
        {
            incomplete = true;
        }

        return new ProjectListResult { Projects = projects, Incomplete = incomplete, Pages = pages };
    }

    public async Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default)
    {
        var items = new List<ConversationIndexItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = "0";
        var pages = 0;
        var incomplete = false;
        var cutoff = false;
        seen.Add("0");
        while (pages < ConversationIndexPager.MaxPages)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ProjectConversationsById(projectId, cursor), cancellationToken: cancellationToken);
            var page = AccountParser.ParseConversationIndex(root, archived: false, projectId, "project");
            foreach (var item in page)
            {
                if (minUpdateTime is double min && item.UpdateTime > 0 && item.UpdateTime < min)
                {
                    cutoff = true;
                    break;
                }

                items.Add(item);
            }

            pages++;
            var info = ConversationIndexPager.ReadCursor(root);
            if (cutoff || !ConversationIndexPager.ShouldFetchNextCursor(cursor, info, seen))
            {
                if (!cutoff && info.HasMore == true && (string.IsNullOrWhiteSpace(info.NextCursor) || info.NextCursor == cursor))
                {
                    incomplete = true;
                }

                break;
            }

            cursor = info.NextCursor;
        }

        if (!cutoff && pages >= ConversationIndexPager.MaxPages)
        {
            incomplete = true;
        }

        return new ConversationIndexResult { Items = items, ReachedCutoff = cutoff, Incomplete = incomplete, Pages = pages };
    }

    public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
        _loader.LoadAsync(_transport, conversationId, GetJsonAsync, cancellationToken);

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

    public async Task<ConversationIndexResult> FetchIndexAsync(bool archived, double? minUpdateTime, CancellationToken cancellationToken)
    {
        var items = new List<ConversationIndexItem>();
        var offset = 0;
        var seenNext = new HashSet<int>();
        var pages = 0;
        var incomplete = false;
        var cutoff = false;
        while (pages < ConversationIndexPager.MaxPages)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ConversationsPage(offset, ConversationIndexPager.RequestedLimit, archived), cancellationToken: cancellationToken);
            var pageItems = AccountParser.ParseConversationIndex(root, archived, source: archived ? "archived" : "chat");
            foreach (var item in pageItems)
            {
                if (minUpdateTime is double min && item.UpdateTime > 0 && item.UpdateTime < min)
                {
                    cutoff = true;
                    break;
                }

                items.Add(item);
            }

            var info = ConversationIndexPager.ReadPage(root, offset, ConversationIndexPager.RequestedLimit, pageItems.Count, cutoff);
            pages++;
            if (!ConversationIndexPager.ShouldFetchNext(info, offset, seenNext))
            {
                if (!cutoff && info.HasMore == true && (info.NextOffset is null || info.NextOffset <= offset))
                {
                    incomplete = true;
                }

                break;
            }

            offset = info.NextOffset ?? (offset + Math.Max(pageItems.Count, 1));
        }

        if (!cutoff && pages >= ConversationIndexPager.MaxPages)
        {
            incomplete = true;
        }

        return new ConversationIndexResult
        {
            Items = items,
            ReachedCutoff = cutoff,
            Incomplete = incomplete,
            Pages = pages
        };
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
