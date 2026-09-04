namespace ProMeter.Models;

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public bool FirstRunCompleted { get; set; }
    public SubscriptionPreset PlanPreset { get; set; } = SubscriptionPreset.Pro100;
    public int WeeklyProQuota { get; set; } = 50;
    public int? DailyProQuota { get; set; }
    public int? SolProDailyQuota { get; set; }
    public int? CombinedDailyQuota { get; set; }
    public int? ReasoningQuota { get; set; }
    public DayOfWeek ResetWeekday { get; set; } = DayOfWeek.Monday;
    public TimeSpan ResetTime { get; set; } = new(0, 0, 0);
    public string ResetTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool ResetAnchorConfigured { get; set; }
    public DisplayMode DisplayMode { get; set; } = DisplayMode.TrayOnly;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public bool AutoSync { get; set; }
    public int SyncIntervalMinutes { get; set; } = 15;
    public int BodyFetchDelayMilliseconds { get; set; } = 250;
    public bool StartWithWindows { get; set; }
    public bool FloatingWidgetEnabled { get; set; }
    public double WidgetLeft { get; set; } = 40;
    public double WidgetTop { get; set; } = 40;
    public double WidgetOpacity { get; set; } = 0.92;
    public bool WidgetAlwaysOnTop { get; set; } = true;
    public bool WidgetClickThrough { get; set; }
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.RemainingNumber;
    public bool FlyoutCloseOnDeactivate { get; set; } = true;
    public bool NotifyAt20 { get; set; } = true;
    public bool NotifyAt10 { get; set; } = true;
    public bool NotifyExhausted { get; set; } = true;
    public bool NotifyReset { get; set; } = true;
    public bool NotifySyncError { get; set; } = true;
    public bool ImportHistoricalStatistics { get; set; }
    public string LastNotifiedPeriodKey { get; set; } = "";
    public int LastNotifiedRemainingBucket { get; set; } = int.MaxValue;
    public bool LastNotifiedExhausted { get; set; }
    public string? LastResetNotifiedPeriod { get; set; }
    public string LastNotifiedDailyKey { get; set; } = "";
    public int LastNotifiedSolDailyBucket { get; set; } = int.MaxValue;
    public bool LastNotifiedSolDailyExhausted { get; set; }
    public int LastNotifiedCombinedDailyBucket { get; set; } = int.MaxValue;
    public bool LastNotifiedCombinedDailyExhausted { get; set; }

    public static AppSettings CreateDefaults() => new();

    public void ApplyPreset(SubscriptionPreset preset)
    {
        PlanPreset = preset;
        switch (preset)
        {
            case SubscriptionPreset.Pro100:
                // Official ChatGPT Help (2026): Pro $100 shares 50 weekly messages
                // across GPT-6 Pro and GPT-5.6 Sol Pro.
                WeeklyProQuota = 50;
                DailyProQuota = null;
                SolProDailyQuota = null;
                CombinedDailyQuota = null;
                break;
            case SubscriptionPreset.Pro200:
                // Official ChatGPT Help (2026): Pro $200 has 200 GPT-6 Pro weekly,
                // 170 Sol Pro daily, and 200 combined daily.
                WeeklyProQuota = 200;
                DailyProQuota = null;
                SolProDailyQuota = 170;
                CombinedDailyQuota = 200;
                break;
        }
    }
}
