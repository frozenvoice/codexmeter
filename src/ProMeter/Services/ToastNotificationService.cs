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

        var remainingPercent = snapshot.Limit <= 0 ? 100 : snapshot.Remaining * 100.0 / snapshot.Limit;
        if (snapshot.Remaining <= 0 && settings.NotifyExhausted && !settings.LastNotifiedExhausted)
        {
            Show("ProMeter", "GPT Pro quota is exhausted.");
            settings.LastNotifiedExhausted = true;
            settings.LastNotifiedRemainingBucket = 0;
        }
        else if (remainingPercent <= 10 && settings.NotifyAt10 && settings.LastNotifiedRemainingBucket > 10)
        {
            Show("ProMeter", "10% of GPT Pro quota remaining.");
            settings.LastNotifiedRemainingBucket = 10;
        }
        else if (remainingPercent <= 20 && settings.NotifyAt20 && settings.LastNotifiedRemainingBucket > 20)
        {
            Show("ProMeter", "20% of GPT Pro quota remaining.");
            settings.LastNotifiedRemainingBucket = 20;
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
}
