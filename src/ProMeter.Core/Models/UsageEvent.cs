namespace ProMeter.Models;

public sealed class UsageEvent
{
    public string Id { get; set; } = "";
    public string? RequestId { get; set; }
    public string ConversationId { get; set; } = "";
    public string? MessageId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? RequestedModel { get; set; }
    public string? ResponseModel { get; set; }
    public string NormalizedModel { get; set; } = "";
    public string RawModel { get; set; } = "";
    public ReasoningEffort ReasoningEffort { get; set; } = ReasoningEffort.Unknown;
    public UsageSource Source { get; set; } = UsageSource.ConversationSync;
    public string? ProjectId { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public QuotaFamily QuotaFamily { get; set; } = QuotaFamily.Unknown;
    public string DedupeKey { get; set; } = "";

    public static string BuildDedupeKey(string conversationId, string? requestId, string? messageId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return "req:" + requestId.Trim();
        }

        return "msg:" + conversationId + ":" + (messageId ?? "");
    }
}
