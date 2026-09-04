namespace ProMeter.Models;

public sealed class QuotaMetadata
{
    public bool Found { get; set; }
    public DateTimeOffset? ResetAt { get; set; }
    public int? Used { get; set; }
    public int? Limit { get; set; }
    public string? FeatureName { get; set; }
    public string? RawSummary { get; set; }
    public bool IsAuthoritative { get; set; }
    public bool MatchesGptProAllowance { get; set; }
}
