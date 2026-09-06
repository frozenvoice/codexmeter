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
    public Dictionary<string, int> SchemaMismatchReasons { get; } = new(StringComparer.Ordinal);

    public int UnresolvedConversationCount => FailedThisSyncCount + DeferredCount;
    public bool HasConversationFailures => UnresolvedConversationCount > 0;

    public void AddThisSync(string? category, int status = 0, string? detail = null)
    {
        FailedThisSyncCount++;
        AddCategory(category, status, detail);
    }

    public void AddDeferred(string? category, string? detail = null)
    {
        DeferredCount++;
        AddCategory(category, 0, detail);
    }

    private void AddCategory(string? category, int status, string? detail)
    {
        var normalized = ConversationFetchBackoff.NormalizeCategory(category, status);
        switch (normalized)
        {
            case ConversationFetchBackoff.BodyTimeout:
                BodyTimeoutCount++;
                break;
            case ConversationFetchBackoff.SchemaMismatch:
                SchemaMismatchCount++;
                AddSchemaMismatchReason(detail);
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

    private void AddSchemaMismatchReason(string? detail)
    {
        var key = SchemaMismatchReason.Normalize(detail);
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        SchemaMismatchReasons[key] = SchemaMismatchReasons.TryGetValue(key, out var count) ? count + 1 : 1;
    }
}
