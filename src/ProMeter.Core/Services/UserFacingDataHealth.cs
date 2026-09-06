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
            var previousUsable = snapshot.LastSync is not null && MeterDataAvailable(snapshot);
            return new UserFacingDataHealth(
                UserFacingHealthKind.Syncing,
                UiText.SyncingEllipsis,
                previousUsable ? UiText.DataUsable : UiText.SyncingEllipsis,
                previousUsable,
                false);
        }

        switch (snapshot.Status)
        {
            case AppSyncStatus.SignedOut:
            case AppSyncStatus.AuthenticationRequired:
                return Actionable(UserFacingHealthKind.NeedsSignIn, UiText.SignInRequired);
            case AppSyncStatus.CompanionDisconnected:
            case AppSyncStatus.ChatGptTabRequired:
            case AppSyncStatus.PageBridgeUnavailable:
            case AppSyncStatus.BridgeTimeout:
            case AppSyncStatus.BridgeWriteFailed:
            case AppSyncStatus.Offline:
                return Actionable(UserFacingHealthKind.NeedsConnection, UiText.ConnectionRequired);
            case AppSyncStatus.Error:
            case AppSyncStatus.Forbidden:
            case AppSyncStatus.RateLimited:
            case AppSyncStatus.ApiChanged:
                return Actionable(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort);
            case AppSyncStatus.DetectingAccount:
            case AppSyncStatus.LoadingCatalog:
            case AppSyncStatus.Syncing:
                return new UserFacingDataHealth(
                    UserFacingHealthKind.Syncing,
                    UiText.SyncingEllipsis,
                    UiText.SyncingEllipsis,
                    false,
                    false);
        }

        if (snapshot.Status == AppSyncStatus.ProviderSchemaMismatch && IsPrimaryProviderSchemaFailure(snapshot))
        {
            return Actionable(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort);
        }

        if (snapshot.Coverage.ConversationSchemaSystemicFailure)
        {
            return SystemicConversationFailure();
        }

        if (snapshot.Status == AppSyncStatus.ProviderSchemaMismatch)
        {
            return HistoryReconstructionOnly(snapshot)
                ? EvaluateCompleted(snapshot)
                : Actionable(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort);
        }

        return EvaluateCompleted(snapshot);
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

        return !snapshot.Coverage.ConversationSchemaSystemicFailure
            && MeterDataAvailable(snapshot)
            && snapshot.Coverage.FailureSummary.HasConversationFailures
            && snapshot.Coverage.NormalIndexState is not CollectionState.Failed;
    }

    private static UserFacingDataHealth EvaluateCompleted(QuotaSnapshot snapshot)
    {
        if (snapshot.Coverage.ConversationSchemaSystemicFailure)
        {
            return SystemicConversationFailure();
        }

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
            && !MeterDataAvailable(snapshot))
        {
            return Actionable(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort);
        }

        if (MeterDataAvailable(snapshot) || snapshot.LastSync is not null)
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

    public static bool ServerStateAvailable(QuotaSnapshot snapshot)
    {
        var status = snapshot.ProServerStatus;
        return status.ServerObserved && status.RestrictionState != ProRestrictionState.Unknown;
    }

    public static bool MeterDataAvailable(QuotaSnapshot snapshot)
    {
        if (snapshot.Coverage.NormalIndexState == CollectionState.Failed)
        {
            return false;
        }

        if (snapshot.CurrentCycleKnown && !snapshot.DisplayUsageUnavailable)
        {
            return true;
        }

        if (ServerStateAvailable(snapshot))
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

    private static bool IsPrimaryProviderSchemaFailure(QuotaSnapshot snapshot) =>
        snapshot.Coverage.NormalIndexState == CollectionState.Failed
        || snapshot.Coverage.HistoryLoadedWithoutUsage;

    private static UserFacingDataHealth Actionable(UserFacingHealthKind kind, string text) =>
        new(kind, text, text, false, true);

    private static UserFacingDataHealth SystemicConversationFailure() =>
        new(UserFacingHealthKind.SyncFailed, UiText.SyncFailedShort, UiText.NeedsAttention, false, true);
}
