namespace ProMeter.Services;

public enum QuotaNotificationKind
{
    PeriodChanged,
    Threshold,
    RestrictionDetected,
    RestrictionCleared
}

public readonly record struct QuotaNotification(QuotaNotificationKind Kind, string Title, string Body);

public static class QuotaNotificationPolicy
{
    public static IReadOnlyList<QuotaNotification> Evaluate(QuotaSnapshot snapshot, AppSettings settings)
    {
        var notifications = new List<QuotaNotification>();
        var periodKey = snapshot.PeriodStart.ToUniversalTime().ToString("O");
        if (!string.Equals(settings.LastNotifiedPeriodKey, periodKey, StringComparison.Ordinal))
        {
            if (settings.NotifyReset
                && !string.IsNullOrWhiteSpace(settings.LastNotifiedPeriodKey)
                && snapshot.UsesServerCount)
            {
                notifications.Add(new QuotaNotification(QuotaNotificationKind.PeriodChanged, UiText.ProductName, UiText.ToastNewPeriod));
            }

            settings.LastNotifiedPeriodKey = periodKey;
            settings.LastNotifiedRemainingBucket = int.MaxValue;
            settings.LastNotifiedExhausted = false;
        }

        if (snapshot.UsesServerCount)
        {
            AddLimit(
                notifications,
                remaining: snapshot.Remaining,
                limit: snapshot.Limit,
                settings: settings,
                getBucket: () => settings.LastNotifiedRemainingBucket,
                setBucket: value => settings.LastNotifiedRemainingBucket = value,
                getExhausted: () => settings.LastNotifiedExhausted,
                setExhausted: value => settings.LastNotifiedExhausted = value,
                exhausted: UiText.ToastGptProExhausted,
                at10: UiText.ToastGptPro10,
                at20: UiText.ToastGptPro20);
        }

        var dayKey = snapshot.PeriodStart.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                     + ":" + DateTimeOffset.Now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!string.Equals(settings.LastNotifiedDailyKey, dayKey, StringComparison.Ordinal))
        {
            settings.LastNotifiedDailyKey = dayKey;
            settings.LastNotifiedSolDailyBucket = int.MaxValue;
            settings.LastNotifiedSolDailyExhausted = false;
            settings.LastNotifiedCombinedDailyBucket = int.MaxValue;
            settings.LastNotifiedCombinedDailyExhausted = false;
        }

        if (snapshot.SolProDailyLimit is int solLimit && snapshot.UsesServerCount)
        {
            AddLimit(
                notifications,
                remaining: snapshot.SolProDailyRemaining,
                limit: solLimit,
                settings: settings,
                getBucket: () => settings.LastNotifiedSolDailyBucket,
                setBucket: value => settings.LastNotifiedSolDailyBucket = value,
                getExhausted: () => settings.LastNotifiedSolDailyExhausted,
                setExhausted: value => settings.LastNotifiedSolDailyExhausted = value,
                exhausted: UiText.ToastSolExhausted,
                at10: UiText.ToastSol10,
                at20: UiText.ToastSol20);
        }

        if (snapshot.CombinedDailyLimit is int combinedLimit && snapshot.UsesServerCount)
        {
            AddLimit(
                notifications,
                remaining: snapshot.CombinedDailyRemaining,
                limit: combinedLimit,
                settings: settings,
                getBucket: () => settings.LastNotifiedCombinedDailyBucket,
                setBucket: value => settings.LastNotifiedCombinedDailyBucket = value,
                getExhausted: () => settings.LastNotifiedCombinedDailyExhausted,
                setExhausted: value => settings.LastNotifiedCombinedDailyExhausted = value,
                exhausted: UiText.ToastCombinedExhausted,
                at10: UiText.ToastCombined10,
                at20: UiText.ToastCombined20);
        }

        AddRestriction(notifications, snapshot, settings);
        return notifications;
    }

    public static void AddRestriction(List<QuotaNotification> notifications, QuotaSnapshot snapshot, AppSettings settings)
    {
        var state = snapshot.ProServerStatus?.RestrictionState ?? ProRestrictionState.Unknown;
        var resetKey = snapshot.ProServerStatus?.ResetAt?.ToUniversalTime().ToString("O") ?? "";
        var key = state + ":" + resetKey;
        if (string.Equals(settings.LastNotifiedProRestrictionKey, key, StringComparison.Ordinal))
        {
            return;
        }

        var previous = settings.LastNotifiedProRestrictionState;
        if (state == ProRestrictionState.CorrelatedRestriction
            && previous != nameof(ProRestrictionState.CorrelatedRestriction))
        {
            var body = snapshot.ProServerStatus?.ResetAt is DateTimeOffset reset
                ? $"{UiText.ToastProRestriction} {UiText.ServerReset}: {DisplayFormatting.FormatStamp(reset)}"
                : UiText.ToastProRestriction;
            notifications.Add(new QuotaNotification(QuotaNotificationKind.RestrictionDetected, UiText.ProductName, body));
        }
        else if (previous == nameof(ProRestrictionState.CorrelatedRestriction)
                 && state == ProRestrictionState.NoCorrelatedRestrictionObserved
                 && settings.NotifyReset)
        {
            notifications.Add(new QuotaNotification(
                QuotaNotificationKind.RestrictionCleared,
                UiText.ProductName,
                UiText.ToastProRestrictionCleared));
        }

        if (state is ProRestrictionState.CorrelatedRestriction or ProRestrictionState.NoCorrelatedRestrictionObserved)
        {
            settings.LastNotifiedProRestrictionKey = key;
            settings.LastNotifiedProRestrictionState = state.ToString();
        }
    }

    private static void AddLimit(
        List<QuotaNotification> notifications,
        int remaining,
        int limit,
        AppSettings settings,
        Func<int> getBucket,
        Action<int> setBucket,
        Func<bool> getExhausted,
        Action<bool> setExhausted,
        string exhausted,
        string at10,
        string at20)
    {
        var remainingPercent = limit <= 0 ? 100 : remaining * 100.0 / limit;
        if (remaining <= 0 && settings.NotifyExhausted && !getExhausted())
        {
            notifications.Add(new QuotaNotification(QuotaNotificationKind.Threshold, UiText.ProductName, exhausted));
            setExhausted(true);
            setBucket(0);
        }
        else if (remainingPercent <= 10 && settings.NotifyAt10 && getBucket() > 10)
        {
            notifications.Add(new QuotaNotification(QuotaNotificationKind.Threshold, UiText.ProductName, at10));
            setBucket(10);
        }
        else if (remainingPercent <= 20 && settings.NotifyAt20 && getBucket() > 20)
        {
            notifications.Add(new QuotaNotification(QuotaNotificationKind.Threshold, UiText.ProductName, at20));
            setBucket(20);
        }
    }
}
