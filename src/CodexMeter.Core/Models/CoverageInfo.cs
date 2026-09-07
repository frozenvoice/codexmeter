namespace CodexMeter.Models;

public sealed class CoverageInfo
{
    public bool NormalChats { get; set; }
    public bool ArchivedChats { get; set; }
    public bool Projects { get; set; }
    public bool TemporaryChats { get; set; }
    public bool DeletedChats { get; set; }
    public bool ResetTimeAuthoritative { get; set; }
    public bool QuotaMetadataAuthoritative { get; set; }
    public bool BranchesIncluded { get; set; }
    public bool IndexIncomplete { get; set; }
    public bool ConversationIncomplete { get; set; }
    public int FailedConversations { get; set; }
    public SyncFailureSummary FailureSummary { get; set; } = new();
    public int LoadedConversations { get; set; }
    public int ConversationsWithEvents { get; set; }
    public int ZeroEventConversations { get; set; }
    public int AssistantLikeNodes { get; set; }
    public int NodesWithModelMetadata { get; set; }
    public bool HistoryLoadedWithoutUsage { get; set; }
    public string? Notes { get; set; }
    public CoverageConfidence CountConfidence { get; set; } = CoverageConfidence.Estimated;
    public CoverageConfidence ResetConfidence { get; set; } = CoverageConfidence.Estimated;
    public ResetAnchorSource ResetAnchorSource { get; set; } = ResetAnchorSource.Default;
    public CollectionState NormalIndexState { get; set; } = CollectionState.Unavailable;
    public CollectionState ArchivedIndexState { get; set; } = CollectionState.Unavailable;
    public CollectionState ProjectsIndexState { get; set; } = CollectionState.Unavailable;
    public int ScanAttempts { get; set; }
    public int UniqueConversations { get; set; }
    public int BodyFetches { get; set; }
    public bool ConversationSchemaSystemicFailure { get; set; }
    public bool PrimaryIndexSchemaMismatch { get; set; }

    public CoverageConfidence Confidence
    {
        get
        {
            if (IndexIncomplete || ConversationIncomplete || FailedConversations > 0)
            {
                return CoverageConfidence.Incomplete;
            }

            return CountConfidence;
        }
    }

    public CollectionState OverallState
    {
        get
        {
            if (HistoryLoadedWithoutUsage)
            {
                return CollectionState.Unavailable;
            }

            var indexes = new[] { NormalIndexState, ArchivedIndexState, ProjectsIndexState };
            if (indexes.All(state => state == CollectionState.Unavailable)
                && LoadedConversations == 0
                && FailedConversations == 0)
            {
                return CollectionState.Unavailable;
            }

            if (FailedConversations > 0
                || IndexIncomplete
                || ConversationIncomplete
                || indexes.Any(state => state is CollectionState.Partial or CollectionState.Failed))
            {
                return CollectionState.Partial;
            }

            if (CountConfidence is CoverageConfidence.Estimated
                || ResetConfidence is CoverageConfidence.Estimated)
            {
                return CollectionState.Estimated;
            }

            return CollectionState.Complete;
        }
    }

    public string SummaryLabel => OverallState switch
    {
        CollectionState.Complete => "Complete",
        CollectionState.Partial => "Partial",
        CollectionState.Estimated => "Estimated",
        _ => "Unavailable"
    };

    public int ApproximatePercent
    {
        get
        {
            var score = 0;
            if (NormalChats) score += 50;
            if (ArchivedChats) score += 15;
            if (Projects) score += 15;
            if (ResetTimeAuthoritative) score += 10;
            if (QuotaMetadataAuthoritative) score += 10;
            if (IndexIncomplete || ConversationIncomplete) score -= 25;
            if (FailedConversations > 0) score -= 10;
            return Math.Clamp(score, 0, 100);
        }
    }
}
