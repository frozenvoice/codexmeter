namespace CodexMeter.Models;

public sealed class ConversationIndexItem
{
    public string Id { get; set; } = "";
    public string? Title { get; set; }
    public double CreateTime { get; set; }
    public double UpdateTime { get; set; }
    public bool Archived { get; set; }
    public string? ProjectId { get; set; }
    public string Source { get; set; } = "chat";
}
