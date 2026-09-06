namespace ProMeter.Services;

public enum UserFacingHealthKind
{
    Usable,
    Syncing,
    NeedsConnection,
    NeedsSignIn,
    SyncFailed,
    Stale,
    NeedsAttention
}

public readonly record struct UserFacingDataHealth(
    UserFacingHealthKind Kind,
    string HeaderText,
    string DataStatusText,
    bool PrimaryDataUsable,
    bool Actionable);

public static class UserFacingHealth
{
    public static UserFacingDataHealth From(QuotaSnapshot snapshot)
    {
        if (snapshot.IsSyncing)
        {
            var previousUsable = snapshot.LastSync is not null && PrimaryLooksUsable(snapshot);
            return new UserFacingDataHealth(
                UserFacingHealthKind.Syncing,
                UiText.SyncingEllipsis,
                snapshot.LastSync is null ? UiText.SyncingEllipsis : UiText.DataUsable,
                previousUsable,
                false);
        }

        return snapshot.Status switch
        {
            AppSyncStatus.SignedOut or AppSyncStatus.AuthenticationRequired
                => Actionable(UserFacingHealthKind.NeedsSignIn, UiText.SignInRequired),
            AppSyncStatus.CompanionDisconnected
                or AppSyncStatus.ChatGptTabRequired
                or AppSyncStatus.PageBridgeUnavailable
                or AppSyncStatus.BridgeTimeout
                or AppSyncStatus.BridgeWriteFailed
                or AppSyncStatus.Offline
                => Actionable(UserFacingHealthKind.NeedsConnection, UiText.ConnectionRequired),
            AppSyncStatus.Error
                or AppSyncStatus.Forbidden
                or AppSyncStatus.RateLimited
                or AppSyncStatus.ApiChanged
                => Actionable(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort),
            AppSyncStatus.ProviderSchemaMismatch
                => HistoryReconstructionOnly(snapshot)
                    ? EvaluateCompleted(snapshot)
                    : Actionable(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort),
            AppSyncStatus.DetectingAccount or AppSyncStatus.LoadingCatalog or AppSyncStatus.Syncing
                => new UserFacingDataHealth(
                    UserFacingHealthKind.Syncing,
                    UiText.SyncingEllipsis,
                    UiText.SyncingEllipsis,
                    false,
                    false),
            _ => EvaluateCompleted(snapshot)
        };
    }

    public static bool HistoryReconstructionOnly(QuotaSnapshot snapshot)
    {
        if (snapshot.Status is not (
            AppSyncStatus.PartialData
            or AppSyncStatus.UpToDate
            or AppSyncStatus.Idle
            or AppSyncStatus.ProviderSchemaMismatch))
        {
            return false;
        }

        return PrimaryLooksUsable(snapshot)
            && snapshot.Coverage.FailureSummary.HasConversationFailures
            && snapshot.Coverage.NormalIndexState is not CollectionState.Failed;
    }

    private static UserFacingDataHealth EvaluateCompleted(QuotaSnapshot snapshot)
    {
        if (IsPrimaryStale(snapshot))
        {
            return new UserFacingDataHealth(
                UserFacingHealthKind.Stale,
                UiText.DataStale,
                UiText.DataStale,
                true,
                true);
        }

        if (snapshot.Coverage.IndexIncomplete
            && snapshot.Coverage.FailedConversations == 0
            && snapshot.Coverage.NormalIndexState == CollectionState.Failed
            && !PrimaryLooksUsable(snapshot))
        {
            return Actionable(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort);
        }

        if (PrimaryLooksUsable(snapshot) || snapshot.LastSync is not null)
        {
            return new UserFacingDataHealth(
                UserFacingHealthKind.Usable,
                UiText.Updated,
                UiText.DataUsable,
                true,
                false);
        }

        return new UserFacingDataHealth(
            UserFacingHealthKind.NeedsAttention,
            UiText.NeedsAttention,
            UiText.NeedsAttention,
            false,
            true);
    }

    private static bool PrimaryLooksUsable(QuotaSnapshot snapshot)
    {
        var status = snapshot.ProServerStatus;
        if (status.ServerObserved && status.RestrictionState != ProRestrictionState.Unknown)
        {
            return true;
        }

        return snapshot.LastSync is not null
            && snapshot.Status is AppSyncStatus.UpToDate or AppSyncStatus.PartialData;
    }

    private static bool IsPrimaryStale(QuotaSnapshot snapshot) =>
        snapshot.ProServerStatus is
        {
            Stale: true,
            ServerObserved: true,
            RestrictionState: not ProRestrictionState.Unknown
        };

    private static UserFacingDataHealth Actionable(UserFacingHealthKind kind, string text) =>
        new(kind, text, text, false, true);
}
