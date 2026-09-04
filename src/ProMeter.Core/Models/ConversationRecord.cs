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
}
