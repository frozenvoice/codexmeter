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
        foreach (var notification in QuotaNotificationPolicy.Evaluate(snapshot, settings))
        {
            Show(notification.Title, notification.Body);
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
}
