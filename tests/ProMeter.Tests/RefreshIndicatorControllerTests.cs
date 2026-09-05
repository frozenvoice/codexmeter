using ProMeter.Codex;
using ProMeter.Models;
using ProMeter.Services;

namespace ProMeter.Tests;

public class RefreshIndicatorControllerTests
{
    [Fact]
    public void Inactive_HasNoRunningAnimation()
    {
        var controller = new RefreshIndicatorController();
        Assert.False(controller.IsAnimating);
        Assert.Equal(0, controller.Angle);
        Assert.Equal(0, controller.StartCount);
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(false));
        Assert.False(controller.IsAnimating);
        Assert.InRange(RefreshIndicatorController.DurationSeconds, 0.8, 1.0);
    }

    [Fact]
    public void Active_StartsRotationOnce()
    {
        var controller = new RefreshIndicatorController();
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.True(controller.IsAnimating);
        Assert.Equal(1, controller.StartCount);
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.Equal(1, controller.StartCount);
        Assert.True(controller.IsAnimating);
    }

    [Fact]
    public void Completion_StopsAndResetsRotation()
    {
        var controller = new RefreshIndicatorController();
        controller.Apply(true);
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Apply(false));
        Assert.False(controller.IsAnimating);
        Assert.Equal(0, controller.Angle);
        Assert.Equal(1, controller.StopCount);
    }

    [Fact]
    public void Failure_StopsAndResetsRotation()
    {
        var controller = new RefreshIndicatorController();
        controller.Apply(true);
        var failed = CombinedRefreshCoordinator.Present(false, false);
        Assert.False(failed.Active);
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Apply(failed.Active));
        Assert.False(controller.IsAnimating);
        Assert.Equal(0, controller.Angle);
    }

    [Fact]
    public void Cancellation_StopsAndResetsRotation()
    {
        var controller = new RefreshIndicatorController();
        controller.Apply(true);
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Reset());
        Assert.False(controller.IsAnimating);
        Assert.Equal(0, controller.Angle);
    }

    [Fact]
    public void LocalizedProgressText_IsVisibleWithoutHover()
    {
        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var english = CombinedRefreshCoordinator.Present(true, false, combinedManual: true);
            Assert.Equal("Refreshing...", english.ProgressText);
            Assert.False(string.IsNullOrWhiteSpace(english.ProgressText));
            UiText.SetLanguage(UiLanguage.Korean);
            var korean = CombinedRefreshCoordinator.Present(true, true, combinedManual: true);
            Assert.Equal("동기화 중...", korean.ProgressText);
            Assert.Equal("동기화 중", UiText.Syncing);
            Assert.Equal("새로고침 중...", UiText.CodexRefreshing);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void HiddenThenShown_RestartsWithoutDuplicatingWhileActive()
    {
        var controller = new RefreshIndicatorController();
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Reset());
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(2, controller.StartCount);
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.Equal(2, controller.StartCount);
    }

    [Fact]
    public void AnimationDoesNotDependOnHover()
    {
        var controller = new RefreshIndicatorController();
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(false));
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.True(controller.IsAnimating);
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Apply(false));
        Assert.False(controller.IsAnimating);
    }
}
