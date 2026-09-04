namespace ProMeter.Providers.ChatGpt;

public sealed class FixtureChatGptProvider : IChatGptProvider
{
    private readonly List<ConversationIndexItem> _index = [];
    private readonly Dictionary<string, JsonNode> _bodies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ProjectInfo> _projects = [];
    private readonly Dictionary<string, List<ConversationIndexItem>> _projectConversations = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ModelCatalogEntry> _catalog = [];
    private readonly AccountStatus _account;
    private readonly QuotaMetadata _quota;

    public FixtureChatGptProvider(
        AccountStatus? account = null,
        IEnumerable<ModelCatalogEntry>? catalog = null,
        QuotaMetadata? quota = null)
    {
        _account = account ?? new AccountStatus { IsSignedIn = true, Email = "fixture@example.com", PlanType = "pro" };
        _catalog = catalog?.ToList() ??
        [
            new ModelCatalogEntry { Slug = "gpt-5-6-pro", Title = "GPT-5.6 Sol Pro" }
        ];
        _quota = quota ?? new QuotaMetadata();
    }

    public void AddConversation(ConversationIndexItem item, JsonNode body)
    {
        _index.Add(item);
        _bodies[item.Id] = body;
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
        }

        _projectConversations[project.Id] = list;
    }

    public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_account);

    public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelCatalogEntry>>(_catalog);

    public Task<IReadOnlyList<ConversationIndexItem>> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationIndexItem>>(Filter(_index.Where(i => i.Archived == archived), minUpdateTime));

    public Task<IReadOnlyList<ConversationIndexItem>> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        GetConversationIndexAsync(true, minUpdateTime, cancellationToken);

    public Task<IReadOnlyList<ProjectInfo>> GetProjectsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ProjectInfo>>(_projects);

    public Task<IReadOnlyList<ConversationIndexItem>> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConversationIndexItem>>(
            _projectConversations.TryGetValue(projectId, out var items)
                ? Filter(items, minUpdateTime)
                : []);

    public Task<JsonNode?> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_bodies.TryGetValue(conversationId, out var body) ? body : null);

    public Task<QuotaMetadata> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_quota);

    public int BodyFetchCount { get; private set; }

    public Task<JsonNode?> TrackedGetConversationMessagesAsync(string conversationId)
    {
        BodyFetchCount++;
        return GetConversationMessagesAsync(conversationId);
    }

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
            if (item.UpdateTime < minUpdateTime)
            {
                break;
            }

            result.Add(item);
        }

        return result;
    }
}
