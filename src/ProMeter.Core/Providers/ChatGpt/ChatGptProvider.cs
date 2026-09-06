using ProMeter.Services;

namespace ProMeter.Providers.ChatGpt;

public sealed class ChatGptProvider : IChatGptProvider
{
    private readonly IChatGptTransport _transport;
    private readonly ConversationDetailLoader _loader;
    private readonly Action<string>? _log;

    public ChatGptProvider(IChatGptTransport transport, Action<string>? log = null)
    {
        _transport = transport;
        _log = log;
        _loader = new ConversationDetailLoader(log: log);
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
        catch (ChatGptProviderException ex) when (ex.IsFatalTransportFailure)
        {
            throw;
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
            catch (ChatGptProviderException ex) when (ex.IsFatalTransportFailure)
            {
                throw;
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
        var mismatch = false;
        while (pages < ConversationIndexPager.MaxPages)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ProjectsSidebarQuery(cursor), cancellationToken: cancellationToken);
            var page = AccountParser.ParseProjects(root);
            pages++;
            if (!page.RecognizedShape || page.SchemaMismatch)
            {
                mismatch = true;
                incomplete = true;
                break;
            }

            incomplete |= page.Incomplete;
            projects.AddRange(page.Projects);
            var info = ConversationIndexPager.ReadCursor(root, IndexCollections.Project);
            if (info.SchemaMismatch)
            {
                mismatch = true;
                incomplete = true;
                break;
            }

            if (!ConversationIndexPager.ShouldFetchNextCursor(cursor, info, seen))
            {
                if (info.HasMore == true)
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

        return new ProjectListResult { Projects = projects, Incomplete = incomplete, SchemaMismatch = mismatch, Pages = pages };
    }

    public async Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default)
    {
        var items = new List<ConversationIndexItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = "0";
        var pages = 0;
        var incomplete = false;
        var mismatch = false;
        var cutoff = false;
        var timestampIncomplete = false;
        seen.Add("0");
        while (pages < ConversationIndexPager.MaxPages)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ProjectConversationsById(projectId, cursor), cancellationToken: cancellationToken);
            var page = AccountParser.ParseConversationIndex(root, archived: false, projectId, "project");
            pages++;
            if (!page.RecognizedShape || page.SchemaMismatch)
            {
                mismatch = true;
                incomplete = true;
                break;
            }

            incomplete |= page.Incomplete;
            timestampIncomplete |= !page.TimestampComplete;
            foreach (var item in page.Items)
            {
                if (item.UpdateTime <= 0)
                {
                    timestampIncomplete = true;
                    items.Add(item);
                    continue;
                }

                if (minUpdateTime is double min && item.UpdateTime < min)
                {
                    cutoff = true;
                    break;
                }

                items.Add(item);
            }

            var info = ConversationIndexPager.ReadCursor(root, IndexCollections.Conversation);
            if (info.SchemaMismatch)
            {
                mismatch = true;
                incomplete = true;
                break;
            }

            if (cutoff || !ConversationIndexPager.ShouldFetchNextCursor(cursor, info, seen))
            {
                if (!cutoff && info.HasMore == true)
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

        return new ConversationIndexResult
        {
            Items = items,
            ReachedCutoff = cutoff,
            Incomplete = incomplete || timestampIncomplete,
            SchemaMismatch = mismatch,
            TimestampIncomplete = timestampIncomplete,
            Pages = pages
        };
    }

    public TimeSpan ConversationEndpointTimeout
    {
        get => _loader.ConversationEndpointTimeout;
        set => _loader.ConversationEndpointTimeout = value;
    }

    public async Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        var load = await _loader.LoadAsync(_transport, conversationId, GetJsonAsync, cancellationToken);
        var category = SyncFailureClassifier.ClassifyLoad(load);
        var result = load.Complete
            ? "success"
            : string.Equals(category, "PayloadTooLarge", StringComparison.OrdinalIgnoreCase)
                ? "payload-too-large"
                : load.SchemaMismatch
                    ? "schema-mismatch"
                    : "incomplete";
        _log?.Invoke($"conversation fetch id={conversationId} result={result}");
        return load;
    }

    public async Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var init = await GetJsonAsync(
                "POST",
                ChatGptEndpoints.ConversationInit,
                """{"conversation_mode_kind":"primary_assistant"}""",
                cancellationToken);
            var parsed = AccountParser.ParseQuotaMetadata(init);
            if (parsed.Found || parsed.ProServerStatus.ServerObserved)
            {
                return parsed;
            }
        }
        catch (ChatGptProviderException ex) when (ex.IsFatalTransportFailure)
        {
            throw;
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
        catch (ChatGptProviderException ex) when (ex.IsFatalTransportFailure)
        {
            throw;
        }
        catch (ChatGptProviderException)
        {
        }

        return new QuotaMetadataSet();
    }

    public async Task<ConversationIndexResult> FetchIndexAsync(bool archived, double? minUpdateTime, CancellationToken cancellationToken)
    {
        var items = new List<ConversationIndexItem>();
        var offset = 0;
        var seenNext = new HashSet<int>();
        var pages = 0;
        var incomplete = false;
        var mismatch = false;
        var cutoff = false;
        var timestampIncomplete = false;
        while (pages < ConversationIndexPager.MaxPages)
        {
            var root = await GetJsonAsync("GET", ChatGptEndpoints.ConversationsPage(offset, ConversationIndexPager.RequestedLimit, archived), cancellationToken: cancellationToken);
            var parsed = AccountParser.ParseConversationIndex(root, archived, source: archived ? "archived" : "chat");
            pages++;
            if (!parsed.RecognizedShape || parsed.SchemaMismatch)
            {
                mismatch = true;
                incomplete = true;
                break;
            }

            incomplete |= parsed.Incomplete;
            timestampIncomplete |= !parsed.TimestampComplete;
            foreach (var item in parsed.Items)
            {
                if (item.UpdateTime <= 0)
                {
                    timestampIncomplete = true;
                    items.Add(item);
                    continue;
                }

                if (minUpdateTime is double min && item.UpdateTime < min)
                {
                    cutoff = true;
                    break;
                }

                items.Add(item);
            }

            var info = ConversationIndexPager.ReadPage(root, offset, ConversationIndexPager.RequestedLimit, parsed.TotalRawItems, cutoff);
            if (!ConversationIndexPager.ShouldFetchNext(info, offset, seenNext))
            {
                if (!cutoff && info.HasMore == true)
                {
                    incomplete = true;
                }

                break;
            }

            offset = info.NextOffset ?? (offset + Math.Max(parsed.TotalRawItems, 1));
        }

        if (!cutoff && pages >= ConversationIndexPager.MaxPages)
        {
            incomplete = true;
        }

        return new ConversationIndexResult
        {
            Items = items,
            ReachedCutoff = cutoff,
            Incomplete = incomplete || timestampIncomplete,
            SchemaMismatch = mismatch,
            TimestampIncomplete = timestampIncomplete,
            Pages = pages
        };
    }

    private async Task<JsonNode?> GetJsonAsync(string method, string path, string? body = null, CancellationToken cancellationToken = default)
    {
        var response = await _transport.SendAsync(method, path, body, cancellationToken);
        if (response.IsPayloadTooLarge)
        {
            throw new ChatGptProviderException("PayloadTooLarge", response.Status, response.RetryAfter);
        }

        if (response.IsChunkProtocolError)
        {
            throw new ChatGptProviderException(CompanionChunkProtocol.ProtocolError, response.Status, response.RetryAfter);
        }

        if (response.SchemaMismatch)
        {
            throw new ChatGptProviderException("Provider schema mismatch", response.Status, response.RetryAfter, schemaMismatch: true);
        }

        if (response.IsChatGptTabRequired)
        {
            throw new ChatGptProviderException(CompanionDiagnostics.NoChatGptTab, 0, response.RetryAfter);
        }

        if (response.IsPageBridgeUnavailable)
        {
            throw new ChatGptProviderException(CompanionDiagnostics.PageBridgeUnavailable, 0, response.RetryAfter);
        }

        if (response.IsCompanionDisconnected)
        {
            throw new ChatGptProviderException(
                response.Error ?? CompanionBridgeProtocol.NotConnectedError,
                0,
                response.RetryAfter);
        }

        if (response.IsBridgeTimeout)
        {
            throw new ChatGptProviderException(
                response.Error ?? CompanionBridgeProtocol.TimeoutError,
                0,
                response.RetryAfter);
        }

        if (response.IsBridgeWriteFailed)
        {
            throw new ChatGptProviderException(
                response.Error ?? CompanionBridgeProtocol.WriteFailedError,
                0,
                response.RetryAfter);
        }

        if (response.IsForbidden)
        {
            throw new ChatGptProviderException(CompanionDiagnostics.Forbidden403, response.Status, response.RetryAfter);
        }

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
