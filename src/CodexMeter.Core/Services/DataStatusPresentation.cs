using CodexMeter.Codex;

namespace CodexMeter.Services;

public sealed class DataStatusPresentation
{
    public required string Headline { get; init; }
    public required IReadOnlyList<string> DefaultLines { get; init; }
    public required string Disclaimer { get; init; }
    public required IReadOnlyList<string> AdvancedLines { get; init; }

    public static DataStatusPresentation From(
        QuotaSnapshot snapshot,
        CodexQuotaSnapshot? codex = null,
        bool executableFound = false)
    {
        var health = UserFacingHealth.From(snapshot);
        var codexSnapshot = codex ?? CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable);
        var defaultLines = new[]
        {
            $"{UiText.ChatGptServerStatus}    {ChatGptServerLabel(snapshot)}",
            $"{UiText.CodexStatusLabel}    {CodexHealthLabel(codexSnapshot)}",
            $"{UiText.HistoryBasedStats}    {UiText.Estimated}"
        };

        return new DataStatusPresentation
        {
            Headline = $"{UiText.DataStatus}: {health.DataStatusText}",
            DefaultLines = defaultLines,
            Disclaimer = UiText.HistoryNotOfficialNote,
            AdvancedLines = Advanced(snapshot, codexSnapshot, executableFound)
        };
    }

    public static string ChatGptServerLabel(QuotaSnapshot snapshot)
    {
        var status = snapshot.ProServerStatus;
        if (status.Stale && status.ServerObserved && status.RestrictionState != ProRestrictionState.Unknown)
        {
            return UiText.Stale;
        }

        return status.ServerObserved && status.RestrictionState != ProRestrictionState.Unknown
            ? UiText.Confirmed
            : UiText.Unavailable;
    }

    public static string CodexHealthLabel(CodexQuotaSnapshot snapshot) => snapshot.Status switch
    {
        CodexQuotaStatus.Available => UiText.Confirmed,
        CodexQuotaStatus.Stale => UiText.Stale,
        CodexQuotaStatus.Refreshing => UiText.SyncingEllipsis,
        CodexQuotaStatus.SignedOut => UiText.SignedOut,
        _ => UiText.Unavailable
    };

    private static IReadOnlyList<string> Advanced(
        QuotaSnapshot snapshot,
        CodexQuotaSnapshot codex,
        bool executableFound)
    {
        var coverage = snapshot.Coverage;
        var lines = new List<string>
        {
            $"{UiText.NormalChats}    {DisplayFormatting.CollectionStateLabel(coverage.NormalIndexState)}",
            $"{UiText.ArchivedChats}    {DisplayFormatting.CollectionStateLabel(coverage.ArchivedIndexState)}",
            $"{UiText.Projects}    {DisplayFormatting.CollectionStateLabel(coverage.ProjectsIndexState)}",
            $"{UiText.ConversationBodies}    {coverage.LoadedConversations} {UiText.Successful}, {coverage.FailedConversations} {UiText.UniqueFailed}",
            $"{UiText.CountBasis}    {(coverage.QuotaMetadataAuthoritative ? UiText.CountBasisServer : UiText.CountBasisReconstructed)}",
            $"{UiText.CountConfidence}    {DisplayFormatting.CountConfidenceLabel(coverage.CountConfidence)}",
            UiText.HistoryConfirmedMinPro(snapshot.DisplayUsageUnavailable
                ? "?"
                : ProStatusPresentation.FormatReconstructedCount(snapshot))
        };

        var failures = DisplayFormatting.CoverageFailureDetailLines(coverage);
        if (failures.Count == 0 && coverage.FailedConversations == 0 && !coverage.FailureSummary.HasConversationFailures)
        {
            lines.Add(UiText.NoDiagnosticIssues);
        }
        else
        {
            lines.AddRange(failures);
        }

        lines.AddRange(CodexDisplayFormatting.DiagnosticLines(codex, executableFound));
        return lines;
    }
}
