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
    public bool IsUnauthorized => Status == 401;
    public bool IsForbidden => Status == 403;
    public bool IsChatGptTabRequired =>
        Status == 0 && string.Equals(Message, CompanionDiagnostics.NoChatGptTab, StringComparison.Ordinal);
    public bool IsPageBridgeUnavailable =>
        Status == 0 && string.Equals(Message, CompanionDiagnostics.PageBridgeUnavailable, StringComparison.Ordinal);
    public bool IsCompanionDisconnected =>
        Status == 0 && BridgeFailureClassification.IsCompanionDisconnected(Message);
    public bool IsBridgeTimeout =>
        Status == 0 && BridgeFailureClassification.IsBridgeTimeout(Message);
    public bool IsBridgeWriteFailed =>
        Status == 0 && BridgeFailureClassification.IsBridgeWriteFailed(Message);
    public bool IsPayloadTooLarge =>
        Status == 0
        && (string.Equals(Message, "PayloadTooLarge", StringComparison.OrdinalIgnoreCase)
            || (Message?.Contains("payload too large", StringComparison.OrdinalIgnoreCase) ?? false));
    public bool IsChunkProtocolError =>
        Status == 0 && string.Equals(Message, CompanionChunkProtocol.ProtocolError, StringComparison.Ordinal);
    public bool IsNetworkUnavailable =>
        Status == 0
        && !SchemaMismatch
        && !IsPayloadTooLarge
        && !IsChunkProtocolError
        && !IsChatGptTabRequired
        && !IsPageBridgeUnavailable
        && !IsCompanionDisconnected
        && !IsBridgeTimeout
        && !IsBridgeWriteFailed;
    public bool IsRateLimited => Status == 429;
    public bool IsServerError => Status is >= 500 and < 600;
    public bool IsOffline => IsNetworkUnavailable;
    public bool IsFatalTransportFailure =>
        IsUnauthorized || IsForbidden || IsRateLimited || IsOffline
        || IsChatGptTabRequired || IsPageBridgeUnavailable
        || IsCompanionDisconnected || IsBridgeTimeout || IsBridgeWriteFailed;
}
