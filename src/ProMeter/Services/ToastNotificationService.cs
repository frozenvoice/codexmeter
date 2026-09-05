using System.Globalization;
using Microsoft.Toolkit.Uwp.Notifications;
using ProMeter.Models;

namespace ProMeter.Services;

public sealed class ToastNotificationService
{
    private readonly SettingsStore _settings;
    private readonly AppLog _log;

    public ToastNotificationService(SettingsStore settings, AppLog log)
    {
        _settings = settings;
        _log = log;
    }

    public void Evaluate(QuotaSnapshot snapshot, AppSettings settings)
    {
        var periodKey = snapshot.PeriodStart.ToUniversalTime().ToString("O");
        if (!string.Equals(settings.LastNotifiedPeriodKey, periodKey, StringComparison.Ordinal))
        {
            if (settings.NotifyReset && !string.IsNullOrWhiteSpace(settings.LastNotifiedPeriodKey))
            {
                Show(UiText.ProductName, UiText.ToastNewPeriod);
            }

            settings.LastNotifiedPeriodKey = periodKey;
            settings.LastNotifiedRemainingBucket = int.MaxValue;
            settings.LastNotifiedExhausted = false;
        }

        NotifyLimit(
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

        if (snapshot.SolProDailyLimit is int solLimit)
        {
            NotifyLimit(
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

        if (snapshot.CombinedDailyLimit is int combinedLimit)
        {
            NotifyLimit(
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

        _settings.Save(settings);
    }

    public bool TrySyncError(
        AppSettings settings,
        AppSyncStatus status,
        string? message,
        SyncOrigin origin,
        DateTimeOffset now)
    {
        if (!settings.NotifySyncError)
        {
            return false;
        }

        var state = SyncErrorToastState.FromSettings(settings);
        if (!SyncErrorNotificationGate.ShouldNotify(state, status, message, origin, now, out var next))
        {
            return false;
        }

        next.WriteTo(settings);
        Show(UiText.ToastSyncTitle, message ?? DisplayFormatting.StatusLabel(status));
        return true;
    }

    public void ResetSyncErrorSuppression(AppSettings settings)
    {
        SyncErrorNotificationGate.Cleared().WriteTo(settings);
        SyncErrorToastState.ClearFailureAttempt(settings);
    }

    public static Action<string, string>? Fallback { get; set; }

    public static void Show(string title, string body)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(body)
                .Show();
        }
        catch
        {
            Fallback?.Invoke(title, body);
        }
    }

    private static void NotifyLimit(
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
            Show(UiText.ProductName, exhausted);
            setExhausted(true);
            setBucket(0);
        }
        else if (remainingPercent <= 10 && settings.NotifyAt10 && getBucket() > 10)
        {
            Show("ProMeter", at10);
            setBucket(10);
        }
        else if (remainingPercent <= 20 && settings.NotifyAt20 && getBucket() > 20)
        {
            Show("ProMeter", at20);
            setBucket(20);
        }
    }
}
