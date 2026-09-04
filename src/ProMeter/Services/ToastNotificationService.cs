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
                Show("ProMeter", "A new GPT Pro quota period has started.");
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
            exhausted: "GPT Pro quota is exhausted.",
            at10: "10% of GPT Pro quota remaining.",
            at20: "20% of GPT Pro quota remaining.");

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
                exhausted: "GPT-5.6 Sol Pro daily quota is exhausted.",
                at10: "10% of Sol Pro daily quota remaining.",
                at20: "20% of Sol Pro daily quota remaining.");
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
                exhausted: "Combined Pro daily quota is exhausted.",
                at10: "10% of combined Pro daily quota remaining.",
                at20: "20% of combined Pro daily quota remaining.");
        }

        _settings.Save(settings);
    }

    public void SyncError(AppSettings settings, string message)
    {
        if (!settings.NotifySyncError)
        {
            return;
        }

        Show("ProMeter sync", message);
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
            Show("ProMeter", exhausted);
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
