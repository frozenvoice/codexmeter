namespace CodexMeter.Models;

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
    public DedupeConfidence DedupeConfidence { get; set; } = DedupeConfidence.High;
    public DateTimeOffset? RequestStartedAt { get; set; }
    public DateTimeOffset? ResponseCompletedAt { get; set; }
    public TimestampProvenance TimestampProvenance { get; set; } = TimestampProvenance.Unspecified;
    public ModelEvidenceConfidence ModelConfidence { get; set; } = ModelEvidenceConfidence.Unspecified;
    public bool PeriodAmbiguous { get; set; }
    public bool Countable { get; set; } = true;
    public UnresolvedEvidenceKind UnresolvedKind { get; set; } = UnresolvedEvidenceKind.None;
    public int ReconstructionVersion { get; set; } = ReconstructionSemantics.Version;
    public string? CorrectionReason { get; set; }
    public string? IdentityAliases { get; set; }

    public bool HasUsableTimestamp =>
        TimestampProvenance is not TimestampProvenance.Unknown
        && CreatedAt != default
        && CreatedAt != DateTimeOffset.MinValue;

    public static string BuildDedupeKey(string conversationId, string? requestId, string? messageId)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            return ScopedRequestKey(conversationId, requestId);
        }

        return "msg:" + conversationId + ":" + (messageId ?? "");
    }

    public static string ScopedRequestKey(string conversationId, string requestId) =>
        "req:" + conversationId + ":" + requestId.Trim();

    public static string? UnscopedRequestId(string? dedupeKey)
    {
        if (string.IsNullOrWhiteSpace(dedupeKey) || !dedupeKey.StartsWith("req:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var value = dedupeKey["req:".Length..];
        var split = value.LastIndexOf(':');
        return split < 0 ? value : value[(split + 1)..];
    }
}
