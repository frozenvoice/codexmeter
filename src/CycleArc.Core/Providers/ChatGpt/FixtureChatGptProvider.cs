namespace CycleArc.Providers.ChatGpt;

public sealed class FixtureChatGptProvider : IChatGptProvider
{
    private readonly List<ConversationIndexItem> _index = [];
    private readonly Dictionary<string, JsonNode> _bodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConversationLoadResult> _loadResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProjectInfo> _projects = [];
    private readonly Dictionary<string, List<ConversationIndexItem>> _projectConversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ModelCatalogEntry> _catalog = [];
    private readonly AccountStatus _account;
    private readonly QuotaMetadataSet _quota;

    public bool IndexIncomplete { get; set; }
    public Func<string, ConversationLoadResult>? LoadOverride { get; set; }

    public FixtureChatGptProvider(
        AccountStatus? account = null,
        IEnumerable<ModelCatalogEntry>? catalog = null,
        QuotaMetadataSet? quota = null)
    {
        _account = account ?? new AccountStatus { IsSignedIn = true, Email = "fixture@example.com", PlanType = "pro" };
        _catalog = catalog?.ToList() ??
        [
            new ModelCatalogEntry { Slug = "gpt-5-6-pro", Title = "GPT-5.6 Sol Pro" }
        ];
        _quota = quota ?? new QuotaMetadataSet();
    }

    public void AddConversation(ConversationIndexItem item, JsonNode body)
    {
        _index.Add(item);
        _bodies[item.Id] = body;
        _loadResults[item.Id] = ConversationDetailLoader.FromFixture(body);
    }

    public void AddConversation(ConversationIndexItem item, ConversationLoadResult result)
    {
        _index.Add(item);
        if (result.Conversation is not null)
        {
            _bodies[item.Id] = result.Conversation;
        }

        _loadResults[item.Id] = result;
    }

    public void AddProject(ProjectInfo project, IEnumerable<(ConversationIndexItem Item, JsonNode Body)> conversations)
    {
        _projects.Add(project);
        var list = new List<ConversationIndexItem>();
        foreach (var (item, body) in conversations)
        {
            item.ProjectId = project.Id;
            item.Source = "project";
            list.Add(item);
            _bodies[item.Id] = body;
            _loadResults[item.Id] = ConversationDetailLoader.FromFixture(body);
        }

        _projectConversations[project.Id] = list;
    }

    public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_account);

    public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelCatalogEntry>>(_catalog);

    public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToIndex(Filter(_index.Where(i => i.Archived == archived), minUpdateTime)));

    public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        GetConversationIndexAsync(true, minUpdateTime, cancellationToken);

    public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProjectListResult { Projects = _projects, Incomplete = IndexIncomplete });

    public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(ToIndex(
            _projectConversations.TryGetValue(projectId, out var items)
                ? Filter(items, minUpdateTime)
                : []));

    public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        if (LoadOverride is not null)
        {
            return Task.FromResult(LoadOverride(conversationId));
        }

        if (_loadResults.TryGetValue(conversationId, out var result))
        {
            return Task.FromResult(result);
        }

        return Task.FromResult(ConversationDetailLoader.FromFixture(null));
    }

    public Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_quota);

    private ConversationIndexResult ToIndex(IReadOnlyList<ConversationIndexItem> items) =>
        new() { Items = items, Incomplete = IndexIncomplete };

    private static List<ConversationIndexItem> Filter(IEnumerable<ConversationIndexItem> items, double? minUpdateTime)
    {
        var list = items
            .OrderByDescending(i => i.UpdateTime)
            .ToList();
        if (minUpdateTime is null)
        {
            return list;
        }

        var result = new List<ConversationIndexItem>();
        foreach (var item in list)
        {
            if (item.UpdateTime <= 0)
            {
                result.Add(item);
                continue;
            }

            if (item.UpdateTime < minUpdateTime)
            {
                continue;
            }

            result.Add(item);
        }

        return result;
    }
}
