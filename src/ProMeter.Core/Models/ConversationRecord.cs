namespace ProMeter.Models;

public sealed class ConversationRecord
{
    public string ConversationId { get; set; } = "";
    public double UpdateTime { get; set; }
    public string? ProjectId { get; set; }
    public bool Archived { get; set; }
    public DateTimeOffset? LastScanned { get; set; }
    public double LastSeenUpdateTime { get; set; }
    public string Source { get; set; } = "chat";
    public DateTimeOffset? LastSuccessfulScan { get; set; }
    public DateTimeOffset? LastErrorAt { get; set; }
    public string? LastError { get; set; }
    public ConversationScanStatus Status { get; set; } = ConversationScanStatus.Unknown;
}

public enum ConversationScanStatus
{
    Unknown,
    Ok,
    Incomplete,
    SchemaMismatch,
    FetchFailed
}
