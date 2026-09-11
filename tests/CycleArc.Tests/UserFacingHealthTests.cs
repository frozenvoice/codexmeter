using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Tests;

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
        coverage.PrimaryIndexSchemaMismatch = true;
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
    public void StaleSystemic_AuthenticationRequired_Wins()
    {
        var snapshot = Restricted(5, AppSyncStatus.AuthenticationRequired, SystemicCoverage());
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.NeedsSignIn, health.Kind);
        Assert.Equal(UiText.SignInRequired, health.HeaderText);
        Assert.Equal(UiText.SignInRequired, health.DataStatusText);
        Assert.NotEqual(UiText.NeedsAttention, health.DataStatusText);
        var view = DataStatusPresentation.From(snapshot, AvailableCodex());
        Assert.Equal($"{UiText.DataStatus}: {UiText.SignInRequired}", view.Headline);
        Assert.DoesNotContain(UiText.SyncFailedShort, view.Headline, StringComparison.Ordinal);
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = UserFacingHealth.From(snapshot);
            Assert.Equal("로그인 필요", korean.HeaderText);
            Assert.Equal("로그인 필요", korean.DataStatusText);
            Assert.NotEqual("확인 필요", korean.DataStatusText);
            Assert.DoesNotContain("동기화 실패", korean.HeaderText, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(AppSyncStatus.CompanionDisconnected)]
    [InlineData(AppSyncStatus.ChatGptTabRequired)]
    [InlineData(AppSyncStatus.PageBridgeUnavailable)]
    [InlineData(AppSyncStatus.BridgeTimeout)]
    [InlineData(AppSyncStatus.BridgeWriteFailed)]
    [InlineData(AppSyncStatus.Offline)]
    public void StaleSystemic_CurrentConnectionStatus_Wins(AppSyncStatus status)
    {
        var snapshot = Restricted(5, status, SystemicCoverage());
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.NeedsConnection, health.Kind);
        Assert.Equal(UiText.ConnectionRequired, health.HeaderText);
        Assert.Equal(UiText.ConnectionRequired, health.DataStatusText);
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = UserFacingHealth.From(snapshot);
            Assert.Equal("연결 필요", korean.HeaderText);
            Assert.Equal("연결 필요", korean.DataStatusText);
            Assert.DoesNotContain("동기화 실패", korean.HeaderText, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Theory]
    [InlineData(AppSyncStatus.Forbidden)]
    [InlineData(AppSyncStatus.RateLimited)]
    public void StaleSystemic_ForbiddenOrRateLimited_KeepsExistingActionableStatus(AppSyncStatus status)
    {
        var snapshot = Restricted(5, status, SystemicCoverage());
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.SyncFailedShort, health.DataStatusText);
        Assert.NotEqual(UiText.NeedsAttention, health.DataStatusText);
    }

    [Fact]
    public void StaleSystemic_IndexSchemaMismatch_RemainsActionablePrimaryFailure()
    {
        var coverage = SystemicCoverage();
        coverage.NormalIndexState = CollectionState.Failed;
        coverage.IndexIncomplete = true;
        coverage.PrimaryIndexSchemaMismatch = true;
        var snapshot = Restricted(5, AppSyncStatus.ProviderSchemaMismatch, coverage);
        snapshot.LastSync = null;
        snapshot.ProServerStatus = new ProServerStatus
        {
            ServerObserved = false,
            RestrictionState = ProRestrictionState.Unknown
        };
        var health = UserFacingHealth.From(snapshot);
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.True(health.Actionable);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.SyncFailedShort, health.DataStatusText);
        Assert.NotEqual(UiText.NeedsAttention, health.DataStatusText);
    }

    [Fact]
    public void PrimaryIndexSchemaMismatch_IsNotDemotedByConversationFailure()
    {
        var coverage = CompleteCoverage();
        coverage.PrimaryIndexSchemaMismatch = true;
        coverage.IndexIncomplete = true;
        coverage.ProjectsIndexState = CollectionState.Failed;
        coverage.FailedConversations = 1;
        coverage.ConversationIncomplete = true;
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        var snapshot = Restricted(34, AppSyncStatus.ProviderSchemaMismatch, coverage);
        Assert.True(UserFacingHealth.MeterDataAvailable(snapshot));
        Assert.False(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.SyncFailed, health.Kind);
        Assert.Equal(UiText.SyncFailedShort, health.HeaderText);
        Assert.Equal(UiText.SyncFailedShort, health.DataStatusText);
        Assert.True(health.Actionable);
        Assert.NotEqual(UiText.DataUsable, health.DataStatusText);
        Assert.NotEqual(UiText.NeedsAttention, health.DataStatusText);
    }

    [Fact]
    public void GenericFailedCollectionState_IsNotPrimarySchemaFailure()
    {
        var coverage = CompleteCoverage();
        coverage.IndexIncomplete = true;
        coverage.ArchivedIndexState = CollectionState.Failed;
        coverage.ProjectsIndexState = CollectionState.Failed;
        coverage.ArchivedChats = false;
        coverage.Projects = false;
        var snapshot = Restricted(34, AppSyncStatus.PartialData, coverage);
        Assert.False(coverage.PrimaryIndexSchemaMismatch);
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.Usable, health.Kind);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.False(health.Actionable);
    }

    [Fact]
    public void IsolatedConversationTimeout_UnknownRestriction_KeepsMeterUsable()
    {
        var coverage = CompleteCoverage();
        coverage.FailedConversations = 1;
        coverage.ConversationIncomplete = true;
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.BodyTimeout);
        var snapshot = new QuotaSnapshot
        {
            Used = 5,
            Limit = 50,
            ReconstructedUsed = 5,
            CurrentCycleKnown = true,
            LastSync = DateTimeOffset.UtcNow,
            Status = AppSyncStatus.PartialData,
            Coverage = coverage,
            ResetAnchorSource = ResetAnchorSource.RetainedServer,
            PeriodStart = new DateTimeOffset(2026, 9, 6, 5, 20, 0, TimeSpan.Zero),
            PeriodEnd = new DateTimeOffset(2026, 9, 13, 5, 20, 0, TimeSpan.Zero),
            ProServerStatus = new ProServerStatus
            {
                ServerObserved = true,
                RestrictionState = ProRestrictionState.Unknown,
                LastConfirmedResetAt = new DateTimeOffset(2026, 9, 6, 5, 20, 14, TimeSpan.Zero)
            }
        };
        var health = UserFacingHealth.From(snapshot);
        Assert.True(UserFacingHealth.MeterDataAvailable(snapshot));
        Assert.True(UserFacingHealth.HistoryReconstructionOnly(snapshot));
        Assert.Equal(UserFacingHealthKind.Usable, health.Kind);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.False(health.Actionable);
        Assert.Equal("5+", ProStatusPresentation.From(snapshot).ConfirmedRequestsText);
        Assert.Equal("P? 5+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Full));
        Assert.Equal("P?5+", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Compact));
        var view = DataStatusPresentation.From(snapshot, AvailableCodex());
        Assert.Equal($"{UiText.DataStatus}: {UiText.DataUsable}", view.Headline);
        Assert.Contains(UiText.Unavailable, string.Join('\n', view.DefaultLines), StringComparison.Ordinal);
        Assert.DoesNotContain(UiText.SyncFailedShort, view.Headline, StringComparison.Ordinal);
        Assert.Contains(UiText.ReadTimeout, string.Join('\n', view.AdvancedLines), StringComparison.Ordinal);

        snapshot.Status = AppSyncStatus.ProviderSchemaMismatch;
        Assert.Equal(UserFacingHealthKind.Usable, UserFacingHealth.From(snapshot).Kind);
        Assert.Equal(UiText.DataUsable, UserFacingHealth.From(snapshot).DataStatusText);
    }

    [Fact]
    public void Syncing_NoPreviousSync()
    {
        var snapshot = new QuotaSnapshot
        {
            IsSyncing = true,
            LastSync = null
        };
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.Syncing, health.Kind);
        Assert.Equal(UiText.SyncingEllipsis, health.HeaderText);
        Assert.Equal(UiText.SyncingEllipsis, health.DataStatusText);
        Assert.False(health.PrimaryDataUsable);
        Assert.False(health.Actionable);
    }

    [Fact]
    public void Syncing_LastSyncExistsButPrimaryUnusable()
    {
        var snapshot = new QuotaSnapshot
        {
            IsSyncing = true,
            LastSync = DateTimeOffset.UtcNow,
            Status = AppSyncStatus.Idle,
            CurrentCycleKnown = false,
            ProServerStatus = new ProServerStatus
            {
                ServerObserved = false,
                RestrictionState = ProRestrictionState.Unknown
            }
        };
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.Syncing, health.Kind);
        Assert.Equal(UiText.SyncingEllipsis, health.HeaderText);
        Assert.Equal(UiText.SyncingEllipsis, health.DataStatusText);
        Assert.False(health.PrimaryDataUsable);
        Assert.False(health.Actionable);
        Assert.NotEqual(UiText.DataUsable, health.DataStatusText);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = UserFacingHealth.From(snapshot);
            Assert.Equal("동기화 중...", korean.HeaderText);
            Assert.Equal("동기화 중...", korean.DataStatusText);
            Assert.DoesNotContain("사용 가능", korean.DataStatusText, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void Syncing_PreviousPrimaryUsable()
    {
        var snapshot = Restricted(34, AppSyncStatus.UpToDate, CompleteCoverage());
        snapshot.IsSyncing = true;
        var health = UserFacingHealth.From(snapshot);
        Assert.Equal(UserFacingHealthKind.Syncing, health.Kind);
        Assert.Equal(UiText.SyncingEllipsis, health.HeaderText);
        Assert.Equal(UiText.DataUsable, health.DataStatusText);
        Assert.True(health.PrimaryDataUsable);
        Assert.False(health.Actionable);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var korean = UserFacingHealth.From(snapshot);
            Assert.Equal("동기화 중...", korean.HeaderText);
            Assert.Equal("사용 가능", korean.DataStatusText);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
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
            Assert.Equal("이번 주기 확인 사용", UiText.ConfirmedProUsage);
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
        Assert.Equal(UiText.ReconstructedCount(4), presentation.ReconstructedText);
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
    public void FlyoutSource_HasNoReconstructedUsage()
    {
        var source = File.ReadAllText(Find("src/CycleArc/UI/FlyoutWindow.xaml"));
        Assert.DoesNotContain("ConfirmedUsagePanel", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ModelRows", source, StringComparison.Ordinal);
        Assert.Contains("CodexRows", source, StringComparison.Ordinal);
    }

    private static CoverageInfo SystemicCoverage()
    {
        var coverage = CompleteCoverage();
        coverage.ConversationSchemaSystemicFailure = true;
        coverage.FailedConversations = 3;
        coverage.ConversationIncomplete = true;
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        coverage.FailureSummary.AddThisSync(ConversationFetchBackoff.SchemaMismatch);
        return coverage;
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
