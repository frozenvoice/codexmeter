namespace CycleArc.Models;

public sealed class UsageObservation
{
    public string Id { get; set; } = "";
    public string ConversationId { get; set; } = "";
    public string? MessageId { get; set; }
    public string? ParentMessageId { get; set; }
    public string? RequestId { get; set; }
    public string Role { get; set; } = "";
    public bool Hidden { get; set; }
    public bool? EndTurn { get; set; }
    public string? Recipient { get; set; }
    public string? RequestedModel { get; set; }
    public string? ResponseModel { get; set; }
    public string? RawModel { get; set; }
    public ReasoningEffort Effort { get; set; } = ReasoningEffort.Unknown;
    public DateTimeOffset? CreatedAt { get; set; }
    public UsageSource Source { get; set; } = UsageSource.ConversationSync;
    public string? ProjectId { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public int ReconstructionVersion { get; set; }
    public string? UserAncestorId { get; set; }
    public bool HasMessage { get; set; }

    public static string ObservationKey(string conversationId, string? messageId, string? fallback) =>
        conversationId + "\n" + (string.IsNullOrWhiteSpace(messageId) ? fallback ?? "" : messageId.Trim());
}
