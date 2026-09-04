using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class OnboardingOutcomeTests
{
    [Fact]
    public void UpToDate_ShowsCountAndAllowsFinish()
    {
        var view = OnboardingOutcomeMapper.From(AppSyncStatus.UpToDate, 12, 50, null);
        Assert.True(view.ShowCount);
        Assert.True(view.AllowFinish);
        Assert.Contains("12 / 50", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialData_ShowsIncompleteWarning()
    {
        var view = OnboardingOutcomeMapper.From(AppSyncStatus.PartialData, 3, 50, null);
        Assert.True(view.ShowCount);
        Assert.True(view.AllowFinish);
        Assert.True(view.AllowRetrySync);
        Assert.Contains("incomplete", view.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuthenticationRequired_DoesNotShowQuotaCount()
    {
        var view = OnboardingOutcomeMapper.From(AppSyncStatus.AuthenticationRequired, 0, 50, null);
        Assert.False(view.ShowCount);
        Assert.False(view.AllowFinish);
        Assert.True(view.AllowSignInAgain);
        Assert.DoesNotContain("0 / 50", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RateLimited_ShowsStatusAndAllowsRetry()
    {
        var view = OnboardingOutcomeMapper.From(AppSyncStatus.RateLimited, 0, 50, null);
        Assert.False(view.ShowCount);
        Assert.True(view.AllowRetrySync);
        Assert.Contains("Rate limited", view.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderSchemaMismatch_DoesNotPresentZeroAsSuccess()
    {
        var view = OnboardingOutcomeMapper.From(AppSyncStatus.ProviderSchemaMismatch, 0, 50, null);
        Assert.False(view.ShowCount);
        Assert.Contains("schema mismatch", view.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("0 / 50", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineAndError_ShowActualFailure()
    {
        var offline = OnboardingOutcomeMapper.From(AppSyncStatus.Offline, 0, 50, "chatgpt.com unreachable");
        Assert.False(offline.ShowCount);
        Assert.Equal("chatgpt.com unreachable", offline.Message);

        var error = OnboardingOutcomeMapper.From(AppSyncStatus.Error, 0, 50, "sync failed");
        Assert.False(error.ShowCount);
        Assert.Equal("sync failed", error.Message);
    }
}
