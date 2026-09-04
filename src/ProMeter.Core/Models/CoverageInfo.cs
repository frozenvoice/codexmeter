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
    public bool BranchesIncluded { get; set; } = true;
    public string? Notes { get; set; }

    public CoverageConfidence Confidence
    {
        get
        {
            if (QuotaMetadataAuthoritative && ResetTimeAuthoritative && NormalChats && ArchivedChats && Projects)
            {
                return CoverageConfidence.Authoritative;
            }

            if (NormalChats && (ArchivedChats || Projects))
            {
                return CoverageConfidence.HighConfidence;
            }

            if (NormalChats)
            {
                return CoverageConfidence.Estimated;
            }

            return CoverageConfidence.Incomplete;
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
            if (NormalChats) score += 55;
            if (ArchivedChats) score += 15;
            if (Projects) score += 15;
            if (ResetTimeAuthoritative) score += 10;
            if (QuotaMetadataAuthoritative) score += 5;
            return Math.Min(100, score);
        }
    }
}
