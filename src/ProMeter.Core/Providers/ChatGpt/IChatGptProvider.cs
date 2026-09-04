namespace ProMeter.Providers.ChatGpt;

public interface IChatGptProvider
{
    Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default);
    Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default);
    Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default);
    Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default);
    Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default);
    Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default);
}

public sealed class ChatGptProviderException : Exception
{
    public ChatGptProviderException(string message, int status = 0, string? retryAfter = null, bool schemaMismatch = false)
        : base(message)
    {
        Status = status;
        RetryAfter = retryAfter;
        SchemaMismatch = schemaMismatch;
    }

    public int Status { get; }
    public string? RetryAfter { get; }
    public bool SchemaMismatch { get; }
    public bool IsUnauthorized => Status is 401 or 403;
    public bool IsRateLimited => Status == 429;
    public bool IsServerError => Status is >= 500 and < 600;
    public bool IsOffline => Status == 0;
}
