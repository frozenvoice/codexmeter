using ProMeter.Codex;
using ProMeter.Services;

namespace ProMeter.Tests;

public class UserFacingHealthTests
{
    [Fact]
    public void CaseA_CompleteHistory_ShowsUpdatedAndUsable()
    {
        var snapshot = Restricted(34, AppSyncStatus.UpToDate, CompleteCoverage());
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.Usable, health.Kind);
        Assert.Equal(UiText.Updated, health.HeaderText);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.False(health.Actionable);
        Assert.DoesNotContain("Partial", health.HeaderText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("conversations", health.DataStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CaseB_HistorySchemaMismatch_DoesNotDominateFlyout()
    {
        var coverage = CompleteCoverage();
        coverage.FailedConversations = 2;
        coverage.ConversationIncomplete = true;
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "conversation collection too large");
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "conversation collection too large");
        var snapshot = Restricted(34, AppSyncStatus.PartialData, coverage);
        Assert.Equal(CollectionState.Partial, snapshot.Coverage.OverallState);
        Assert.Equal(CoverageConfidence.Incomplete, snapshot.Coverage.Confidence);

        var health = UserFacingHealth.From(snapshot);
        Assert.True(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        Assert.Equal(UserFacingHealthKind.Usable, health.Kind);
        Assert.Equal(UiText.Updated, health.HeaderText);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.DoesNotContain(UiText.PartialData, health.HeaderText, StringComparison.Ordinal);
        Assert.DoesNotContain("2", health.DataStatusText, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.ConversationsNotRead(2), health.HeaderText, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.ConversationsNotRead(2), health.DataStatusText, StringComparison.Ordinal);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = UserFacingHealth.From(snapshot);
            Assert.Equal("업데이트됨", korean.HeaderText);
            Assert.Equal("사용 가능", korean.DataStatusText);
            Assert.DoesNotContain("일부 데이터 누락", korean.HeaderText, StringComparison.Ordinal);
            Assert.DoesNotContain("읽기 실패", korean.DataStatusText, StringComparison.Ordinal);
            var view = DataStatusPresentation.From(snapshot, AvailableCodex());
            Assert.DoesNotContain("응답 형식 불일치", string.Join('\n', view.DefaultLines), StringComparison.Ordinal);
            Assert.DoesNotContain("이번 동기화 실패", string.Join('\n', view.DefaultLines), StringComparison.Ordinal);
            Assert.DoesNotContain("재시도 대기", string.Join('\n', view.DefaultLines), StringComparison.Ordinal);
            Assert.DoesNotContain("대화 노드 수", string.Join('\n', view.DefaultLines), StringComparison.Ordinal);
            Assert.Contains("응답 형식 불일치", string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void CaseB_ProviderSchemaMismatchStatus_DoesNotDominateWhenPrimaryUsable()
    {
        var coverage = CompleteCoverage();
        coverage.FailedConversations = 2;
        coverage.ConversationIncomplete = true;
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "conversation collection too large");
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "conversation collection too large");
        var snapshot = Restricted(34, AppSyncStatus.ProviderSchemaMismatch, coverage);
        var health = UserFacingHealth.From(snapshot);
        Assert.True(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        Assert.Equal(UserFacingHealthKind.Usable, health.Kind);
        Assert.Equal(UiText.Updated, health.HeaderText);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.False(health.Actionable);
    }

    [Fact]
    public void GlobalProviderSchemaMismatch_RemainsActionable()
    {
        var coverage = CompleteCoverage();
        coverage.NormalIndexState = CollectionState.Failed;
        coverage.IndexIncomplete = true;
        var snapshot = Restricted(34, AppSyncStatus.ProviderSchemaMismatch, coverage);
        snapshot.LastSync = null;
        snapshot.ProServerStatus = new ProServerStatus
        {
            ServerObserved = false,
            RestrictionState = ProRestrictionState.Unknown
        };
        var health = UserFacingHealth.From(snapshot);
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        Assert.True(health.Actionable);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
    }

    [Fact]
    public void CaseC_CompanionDisconnected_IsActionable()
    {
        var snapshot = Restricted(34, AppSyncStatus.CompanionDisconnected, CompleteCoverage());
        var health = UserFacingHealth.From(snapshot);
        Assert.True(health.Actionable);
        Assert.Equal(UserFacingHealthKind.NeedsConnection, health.Kind);
        Assert.Equal(UiText.ConnectionRequired, health.HeaderText);
        Assert.Equal(UiText.ConnectionRequired, health.DataStatusText);
    }

    [Fact]
    public void CaseD_AuthenticationRequired_IsActionable()
    {
        var snapshot = new QuotaSnapshot { Status = AppSyncStatus.AuthenticationRequired };
        var health = UserFacingHealth.From(snapshot);
        Assert.True(health.Actionable);
        Assert.Equal(UserFacingHealthKind.NeedsSignIn, health.Kind);
        Assert.Equal(UiText.SignInRequired, health.HeaderText);
    }

    [Fact]
    public void CaseE_PartialHistory_DoesNotInventExactRemaining()
    {
        var coverage = CompleteCoverage();
        coverage.FailedConversations = 2;
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "mapping incomplete");
        var snapshot = Restricted(34, AppSyncStatus.PartialData, coverage);
        snapshot.Limit = 50;
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.False(presentation.ExactRemainingAvailable);
        Assert.Equal(UiText.ExactRemainingUnavailable, presentation.ExactRemainingText);
        Assert.Equal("34+", presentation.ReconstructedText);
        Assert.True(presentation.ShowHistoryLowerBound);
        Assert.Equal(UiText.HistoryBasedLowerBound, presentation.HistoryLowerBoundCaption);
        Assert.Equal(UiText.HistoryBasedLowerBound, presentation.CountSourceText);
        Assert.EndsWith("+", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
        Assert.DoesNotContain("34 / 50", DisplayFormatting.UsageLabel(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("50 / 50", DisplayFormatting.UsageLabel(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("16", presentation.ExactRemainingText, StringComparison.Ordinal);
        Assert.DoesNotContain("0", presentation.ExactRemainingText, StringComparison.Ordinal);
        Assert.DoesNotContain("34+", DisplayFormatting.FlyoutHeader(snapshot), StringComparison.Ordinal);
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = ProStatusPresentation.From(snapshot);
            Assert.Equal("확인된 Pro 사용", UiText.ConfirmedProUsage);
            Assert.Equal("기록 기반 최소치", korean.HistoryLowerBoundCaption);
            Assert.Equal("34+", korean.ConfirmedRequestsText);
            Assert.Equal("확인 불가", korean.ExactRemainingText);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void CaseF_AuthoritativeExactCount_MayAppear()
    {
        var snapshot = Restricted(7, AppSyncStatus.UpToDate, CompleteCoverage());
        snapshot.UsesServerWeeklyCount = true;
        snapshot.Used = 7;
        snapshot.Limit = 50;
        snapshot.ReconstructedUsed = 4;
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.True(presentation.ExactRemainingAvailable);
        Assert.False(presentation.ShowHistoryLowerBound);
        Assert.Equal("", presentation.HistoryLowerBoundCaption);
        Assert.Equal("7 / 50", DisplayFormatting.UsageLabel(snapshot));
        Assert.Equal("43", presentation.ExactRemainingText);
        Assert.Equal("4+", presentation.ReconstructedText);
    }

    [Fact]
    public void CaseG_SuccessfulRetry_ClearsCurrentFailurePresentation()
    {
        var failedCoverage = CompleteCoverage();
        failedCoverage.FailedConversations = 2;
        failedCoverage.ConversationIncomplete = true;
        failedCoverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "conversation collection too large");
        failedCoverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "conversation collection too large");
        var failed = Restricted(34, AppSyncStatus.PartialData, failedCoverage);
        Assert.Contains(
            "conversation collection too large",
            string.Join('\n', DataStatusPresentation.From(failed, AvailableCodex()).AdvancedLines),
            StringComparison.Ordinal);

        var recovered = Restricted(34, AppSyncStatus.UpToDate, CompleteCoverage());
        recovered.LastSync = DateTimeOffset.UtcNow;
        var view = DataStatusPresentation.From(recovered, AvailableCodex());
        Assert.Equal($"{UiText.DataStatus}: {UiText.DataUsable}", view.Headline);
        Assert.DoesNotContain("conversation collection too large", string.Join('\n', view.DefaultLines), StringComparison.Ordinal);
        Assert.DoesNotContain("Response format mismatch", string.Join('\n', view.DefaultLines), StringComparison.Ordinal);
        Assert.Contains(UiText.NoDiagnosticIssues, view.AdvancedLines);
        Assert.DoesNotContain("conversation collection too large", string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);
        Assert.Equal(UiText.Updated, UserFacingHealth.From(recovered).HeaderText);
        Assert.Equal(0, recovered.Coverage.FailedConversations);
        Assert.False(recovered.Coverage.FailureSummary.HasConversationFailures);
    }

    [Fact]
    public void DataStatusDefault_OmitsParserCounters()
    {
        var coverage = CompleteCoverage();
        coverage.FailedConversations = 1;
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch, 0, "conversation collection too large");
        var snapshot = Restricted(34, AppSyncStatus.PartialData, coverage);
        var view = DataStatusPresentation.From(snapshot, AvailableCodex());
        var defaults = string.Join('\n', view.DefaultLines);
        Assert.Contains(UiText.ChatGptServerStatus, defaults, StringComparison.Ordinal);
        Assert.Contains(UiText.Confirmed, defaults, StringComparison.Ordinal);
        Assert.Contains(UiText.HistoryBasedStats, defaults, StringComparison.Ordinal);
        Assert.Contains(UiText.Estimated, defaults, StringComparison.Ordinal);
        Assert.Contains(UiText.HistoryNotOfficialNote, view.Disclaimer, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.ResponseFormatMismatch, defaults, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.FailedThisSync, defaults, StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.WaitingToRetry, defaults, StringComparison.Ordinal);
        Assert.DoesNotContain("conversation collection too large", defaults, StringComparison.Ordinal);
        Assert.Contains(UiText.HistoryConfirmedMinPro("34+"), view.AdvancedLines);
        Assert.Contains(UiText.ResponseFormatMismatch, string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);
    }

    [Fact]
    public void FlyoutSource_ShowsLowerBoundUsageAndHidesModelBreakdown()
    {
        var xaml = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml"));
        var code = File.ReadAllText(Find("src/ProMeter/UI/FlyoutWindow.xaml.cs"));
        Assert.Contains("ConfirmedUsagePanel", xaml, StringComparison.Ordinal);
        Assert.Contains("ConfirmedUsageText", xaml, StringComparison.Ordinal);
        Assert.Contains("HistoryLowerBoundCaption", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowHistoryLowerBound", code, StringComparison.Ordinal);
        Assert.Contains("HistoryStatsPanel.Visibility = Visibility.Collapsed", code, StringComparison.Ordinal);
        Assert.Contains("ModelRows.Visibility = Visibility.Collapsed", code, StringComparison.Ordinal);
        Assert.Contains("ConfirmedProUsage", code, StringComparison.Ordinal);
        Assert.Contains("HistoryBasedLowerBound", code, StringComparison.Ordinal);
        Assert.Contains("RemainingCount", code, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusLabel(snapshot)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelBreakdown", code, StringComparison.Ordinal);
    }

    private static CoverageInfo CompleteCoverage() => new()
    {
        NormalIndexState = CollectionState.Complete,
        ArchivedIndexState = CollectionState.Complete,
        ProjectsIndexState = CollectionState.Complete,
        CountConfidence = CoverageConfidence.HighConfidence,
        ResetConfidence = CoverageConfidence.Authoritative,
        ResetAnchorSource = ResetAnchorSource.Server
    };

    private static QuotaSnapshot Restricted(int reconstructed, AppSyncStatus status, CoverageInfo coverage) => new()
    {
        Used = reconstructed,
        Limit = 50,
        ReconstructedUsed = reconstructed,
        LastSync = DateTimeOffset.UtcNow,
        Status = status,
        Coverage = coverage,
        ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.CorrelatedRestriction,
            ResetAt = new DateTimeOffset(2026, 9, 6, 5, 20, 0, TimeSpan.Zero),
            ResetConfidence = ServerResetConfidence.Server,
            ObservedAt = DateTimeOffset.UtcNow
        }
    };

    private static CodexQuotaSnapshot AvailableCodex() => new(
        CodexQuotaStatus.Available,
        null,
        DateTimeOffset.Now,
        DateTimeOffset.Now,
        null,
        null,
        null,
        [new CodexQuotaWindow(null, 42, 10080, null, CodexWindowKind.Weekly)],
        null);

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
