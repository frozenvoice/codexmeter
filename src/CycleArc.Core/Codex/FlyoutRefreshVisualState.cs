namespace CycleArc.Codex;

public readonly record struct FlyoutRefreshVisualState(
    bool SyncingTextVisible,
    bool IdleIconVisible,
    bool SpinnerVisible,
    bool RunAnimation)
{
    public static FlyoutRefreshVisualState Create(bool refreshing, bool windowVisible)
    {
        var motion = refreshing && windowVisible;
        return new(
            SyncingTextVisible: refreshing,
            IdleIconVisible: !motion,
            SpinnerVisible: motion,
            RunAnimation: motion);
    }
}
