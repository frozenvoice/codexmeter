using ProMeter.Providers.ChatGpt;

namespace ProMeter.Services;

/// <summary>
/// Derives canonical requests from the retained observation ledger. The current network
/// response is only new evidence; the union of persisted observations is the reconstruction input.
/// </summary>
public sealed class ReconstructionLedger
{
    private readonly ModelNormalizer _models;
    private readonly IClock _clock;

    public ReconstructionLedger(ModelNormalizer models, IClock? clock = null)
    {
        _models = models;
        _clock = clock ?? SystemClock.Instance;
    }

    public IReadOnlyList<UsageEvent> Rebuild(IReadOnlyList<UsageObservation> retained, ConversationRecord record)
    {
        if (retained.Count == 0)
        {
            return [];
        }

        var context = new ConversationParseContext
        {
            ConversationId = record.ConversationId,
            ProjectId = record.ProjectId,
            Archived = record.Archived,
            Source = SourceFor(retained),
            UpdateTime = record.UpdateTime
        };

        return RequestCanonicalizer.Canonicalize(retained, _models, context, _clock);
    }

    private static UsageSource SourceFor(IReadOnlyList<UsageObservation> retained)
    {
        if (retained.Any(o => o.Source == UsageSource.ProjectSync))
        {
            return UsageSource.ProjectSync;
        }

        return retained.Any(o => o.Source == UsageSource.ArchivedSync)
            ? UsageSource.ArchivedSync
            : UsageSource.ConversationSync;
    }
}
