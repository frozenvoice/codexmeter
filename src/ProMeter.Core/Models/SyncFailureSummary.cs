using ProMeter.Services;

namespace ProMeter.Models;

public sealed class SyncFailureSummary
{
    public int FailedThisSyncCount { get; set; }
    public int DeferredCount { get; set; }
    public int BodyTimeoutCount { get; set; }
    public int SchemaMismatchCount { get; set; }
    public int PayloadTooLargeCount { get; set; }
    public int CompanionDisconnectedCount { get; set; }
    public int AuthenticationCount { get; set; }
    public int OtherConversationFailureCount { get; set; }

    public int UnresolvedConversationCount => FailedThisSyncCount + DeferredCount;
    public bool HasConversationFailures => UnresolvedConversationCount > 0;

    public void AddThisSync(string? category, int status = 0)
    {
        FailedThisSyncCount++;
        AddCategory(category, status);
    }

    public void AddDeferred(string? category)
    {
        DeferredCount++;
        AddCategory(category, 0);
    }

    private void AddCategory(string? category, int status)
    {
        switch (ConversationFetchBackoff.NormalizeCategory(category, status))
        {
            case ConversationFetchBackoff.BodyTimeout:
                BodyTimeoutCount++;
                break;
            case ConversationFetchBackoff.SchemaMismatch:
                SchemaMismatchCount++;
                break;
            case ConversationFetchBackoff.PayloadTooLarge:
                PayloadTooLargeCount++;
                break;
            case ConversationFetchBackoff.CompanionDisconnected:
                CompanionDisconnectedCount++;
                break;
            case ConversationFetchBackoff.Authentication:
                AuthenticationCount++;
                break;
            default:
                OtherConversationFailureCount++;
                break;
        }
    }
}
