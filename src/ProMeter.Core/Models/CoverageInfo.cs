namespace ProMeter.Models;

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
    public string? Notes { get; set; }
    public CoverageConfidence CountConfidence { get; set; } = CoverageConfidence.Estimated;
    public CoverageConfidence ResetConfidence { get; set; } = CoverageConfidence.Estimated;
    public ResetAnchorSource ResetAnchorSource { get; set; } = ResetAnchorSource.Default;

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

    public string SummaryLabel => Confidence switch
    {
        CoverageConfidence.Authoritative => "Authoritative",
        CoverageConfidence.HighConfidence => "Good",
        CoverageConfidence.Estimated => "Estimated",
        _ => "Incomplete"
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
