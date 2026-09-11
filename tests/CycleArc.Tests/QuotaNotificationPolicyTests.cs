using CycleArc.Services;

namespace CycleArc.Tests;

public class QuotaNotificationPolicyTests
{
    [Fact]
    public void ReconstructedThresholds_DoNotNotify()
    {
        var settings = NotifyAll();
        var snapshot = new QuotaSnapshot
        {
            Used = 45,
            Limit = 50,
            ReconstructedUsed = 45,
            UsesServerCount = false,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-2)
        };
        Assert.DoesNotContain(QuotaNotificationPolicy.Evaluate(snapshot, settings), item => item.Kind == QuotaNotificationKind.Threshold);

        snapshot.Used = 50;
        Assert.DoesNotContain(QuotaNotificationPolicy.Evaluate(snapshot, settings), item => item.Kind == QuotaNotificationKind.Threshold);
    }

    [Fact]
    public void AuthoritativeCount_StillPermitsThresholdNotification()
    {
        var settings = NotifyAll();
        var snapshot = new QuotaSnapshot
        {
            Used = 45,
            Limit = 50,
            UsesServerCount = true,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var first = QuotaNotificationPolicy.Evaluate(snapshot, settings);
        Assert.Contains(first, item => item.Kind == QuotaNotificationKind.Threshold && item.Body == UiText.ToastGptPro10);
    }

    [Fact]
    public void WeeklyAuthority_DoesNotGateSolDailyThreshold()
    {
        var settings = NotifyAll();
        var snapshot = new QuotaSnapshot
        {
            Used = 10,
            Limit = 50,
            UsesServerWeeklyCount = true,
            UsesServerSolDailyCount = false,
            TodaySolPro = 95,
            SolProDailyLimit = 100,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var notifications = QuotaNotificationPolicy.Evaluate(snapshot, settings);
        Assert.DoesNotContain(notifications, item => item.Body == UiText.ToastSol10 || item.Body == UiText.ToastSol20);
    }

    [Fact]
    public void WeeklyAuthority_DoesNotGateCombinedDailyThreshold()
    {
        var settings = NotifyAll();
        var snapshot = new QuotaSnapshot
        {
            Used = 10,
            Limit = 50,
            UsesServerWeeklyCount = true,
            UsesServerCombinedDailyCount = false,
            CombinedToday = 190,
            CombinedDailyLimit = 200,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var notifications = QuotaNotificationPolicy.Evaluate(snapshot, settings);
        Assert.DoesNotContain(
            notifications,
            item => item.Body == UiText.ToastCombined10 || item.Body == UiText.ToastCombined20);
    }

    [Fact]
    public void SolDailyAuthoritative_PermitsThresholdNotification()
    {
        var settings = NotifyAll();
        var snapshot = new QuotaSnapshot
        {
            UsesServerSolDailyCount = true,
            TodaySolPro = 90,
            SolProDailyLimit = 100,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var first = QuotaNotificationPolicy.Evaluate(snapshot, settings);
        Assert.Contains(first, item => item.Kind == QuotaNotificationKind.Threshold && item.Body == UiText.ToastSol10);
    }

    [Fact]
    public void CombinedDailyAuthoritative_PermitsThresholdNotification()
    {
        var settings = NotifyAll();
        var snapshot = new QuotaSnapshot
        {
            UsesServerCombinedDailyCount = true,
            CombinedToday = 180,
            CombinedDailyLimit = 200,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-2)
        };
        var first = QuotaNotificationPolicy.Evaluate(snapshot, settings);
        Assert.Contains(first, item => item.Kind == QuotaNotificationKind.Threshold && item.Body == UiText.ToastCombined10);
    }

    [Fact]
    public void NewCorrelatedRestriction_NotifiesOnce()
    {
        var settings = NotifyAll();
        var snapshot = Restricted();
        var first = QuotaNotificationPolicy.Evaluate(snapshot, settings);
        Assert.Contains(first, item => item.Kind == QuotaNotificationKind.RestrictionDetected);
        Assert.Contains(first, item => item.Body.Contains(UiText.ToastProRestriction, StringComparison.Ordinal));

        var second = QuotaNotificationPolicy.Evaluate(snapshot, settings);
        Assert.DoesNotContain(second, item => item.Kind == QuotaNotificationKind.RestrictionDetected);
    }

    [Fact]
    public void RestrictionClear_NotifiesOnceWhenEnabled()
    {
        var settings = NotifyAll();
        QuotaNotificationPolicy.Evaluate(Restricted(), settings);
        var cleared = new QuotaSnapshot
        {
            UsesServerCount = false,
            PeriodStart = DateTimeOffset.UtcNow.AddDays(-2),
            ProServerStatus = new ProServerStatus
            {
                ServerObserved = true,
                RestrictionState = ProRestrictionState.NoCorrelatedRestrictionObserved
            }
        };
        var first = QuotaNotificationPolicy.Evaluate(cleared, settings);
        Assert.Contains(first, item => item.Kind == QuotaNotificationKind.RestrictionCleared);
        var second = QuotaNotificationPolicy.Evaluate(cleared, settings);
        Assert.DoesNotContain(second, item => item.Kind == QuotaNotificationKind.RestrictionCleared);
    }

    private static AppSettings NotifyAll()
    {
        var settings = AppSettings.CreateDefaults();
        settings.NotifyAt20 = true;
        settings.NotifyAt10 = true;
        settings.NotifyExhausted = true;
        settings.NotifyReset = true;
        return settings;
    }

    private static QuotaSnapshot Restricted() => new()
    {
        Used = 31,
        Limit = 50,
        UsesServerCount = false,
        PeriodStart = DateTimeOffset.UtcNow.AddDays(-2),
        ProServerStatus = new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.CorrelatedRestriction,
            ResetAt = new DateTimeOffset(2026, 9, 6, 5, 20, 13, TimeSpan.Zero),
            ResetConfidence = ServerResetConfidence.Server
        }
    };
}
