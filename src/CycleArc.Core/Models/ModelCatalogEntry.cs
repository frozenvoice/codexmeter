namespace CycleArc.Models;

public sealed class ModelCatalogEntry
{
    public string Slug { get; set; } = "";
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Tags { get; set; }
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;
}
