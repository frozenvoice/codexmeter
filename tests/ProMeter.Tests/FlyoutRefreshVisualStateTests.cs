using ProMeter.Codex;

namespace ProMeter.Tests;

public class FlyoutRefreshVisualStateTests
{
    [Fact]
    public void Idle_HidesMotionAndShowsStaticIcon()
    {
        var idle = FlyoutRefreshVisualState.Create(false, true);
        Assert.False(idle.SyncingTextVisible);
        Assert.True(idle.IdleIconVisible);
        Assert.False(idle.SpinnerVisible);
        Assert.False(idle.ProgressStripVisible);
        Assert.False(idle.RunAnimation);
    }

    [Fact]
    public void RefreshingVisible_ShowsSpinnerStripAndSyncingText()
    {
        var active = FlyoutRefreshVisualState.Create(true, true);
        Assert.True(active.SyncingTextVisible);
        Assert.False(active.IdleIconVisible);
        Assert.True(active.SpinnerVisible);
        Assert.True(active.ProgressStripVisible);
        Assert.True(active.RunAnimation);
        Assert.Equal(active.SpinnerVisible, active.ProgressStripVisible);
        Assert.Equal(active.IdleIconVisible, !active.RunAnimation);
    }

    [Fact]
    public void RefreshingToRefreshing_DoesNotChangeVisualPolicy()
    {
        var first = FlyoutRefreshVisualState.Create(true, true);
        var second = FlyoutRefreshVisualState.Create(true, true);
        Assert.Equal(first, second);
        var controller = new RefreshIndicatorController();
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.Equal(1, controller.StartCount);
        Assert.True(controller.IsAnimating);
    }

    [Fact]
    public void RefreshingToIdle_StopsMotion()
    {
        var controller = new RefreshIndicatorController();
        controller.Apply(true);
        var idle = FlyoutRefreshVisualState.Create(false, true);
        Assert.False(idle.RunAnimation);
        Assert.True(idle.IdleIconVisible);
        Assert.False(idle.SpinnerVisible);
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Apply(false));
        Assert.False(controller.IsAnimating);
    }

    [Fact]
    public void HiddenThenShownWhileRefreshing_RestartsWithoutStacking()
    {
        var hidden = FlyoutRefreshVisualState.Create(true, false);
        Assert.True(hidden.SyncingTextVisible);
        Assert.True(hidden.IdleIconVisible);
        Assert.False(hidden.SpinnerVisible);
        Assert.False(hidden.ProgressStripVisible);
        Assert.False(hidden.RunAnimation);

        var shown = FlyoutRefreshVisualState.Create(true, true);
        Assert.True(shown.SpinnerVisible);
        Assert.True(shown.ProgressStripVisible);
        Assert.True(shown.RunAnimation);
        Assert.False(shown.IdleIconVisible);

        var controller = new RefreshIndicatorController();
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(RefreshIndicatorTransition.Stopped, controller.Reset());
        Assert.Equal(RefreshIndicatorTransition.Started, controller.Apply(true));
        Assert.Equal(2, controller.StartCount);
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.Equal(2, controller.StartCount);
    }

    [Fact]
    public void HiddenThenShownWhileIdle_StaysIdle()
    {
        var hidden = FlyoutRefreshVisualState.Create(false, false);
        var shown = FlyoutRefreshVisualState.Create(false, true);
        Assert.False(hidden.RunAnimation);
        Assert.False(shown.RunAnimation);
        Assert.True(shown.IdleIconVisible);
        Assert.False(shown.SpinnerVisible);
        var controller = new RefreshIndicatorController();
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(false));
        Assert.Equal(RefreshIndicatorTransition.None, controller.Reset());
    }

    [Fact]
    public void ClickWhileRefreshing_DoesNotResetAnimationState()
    {
        var before = FlyoutRefreshVisualState.Create(true, true);
        var afterClick = FlyoutRefreshVisualState.Create(true, true);
        Assert.Equal(before, afterClick);
        var controller = new RefreshIndicatorController();
        controller.Apply(true);
        Assert.Equal(RefreshIndicatorTransition.None, controller.Apply(true));
        Assert.True(controller.IsAnimating);
        Assert.Equal(1, controller.StartCount);
        Assert.Equal(0, controller.StopCount);
    }
}
