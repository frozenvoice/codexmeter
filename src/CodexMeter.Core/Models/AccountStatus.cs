namespace CodexMeter.Models;

public sealed class AccountStatus
{
    public bool IsSignedIn { get; set; }
    public string? UserId { get; set; }
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public string? PlanType { get; set; }
    public string? PlanBucket { get; set; }
    public bool HasActiveSubscription { get; set; }
    public DateTimeOffset? ObservedAt { get; set; }
}
