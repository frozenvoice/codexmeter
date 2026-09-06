namespace ProMeter.Codex;

public readonly record struct FlyoutRefreshVisualState(
    bool SyncingTextVisible,
    bool IdleIconVisible,
    bool SpinnerVisible,
    bool ProgressStripVisible,
    bool RunAnimation)
{
    public static FlyoutRefreshVisualState Create(bool refreshing, bool windowVisible)
    {
        var motion = refreshing && windowVisible;
        return new(
            SyncingTextVisible: refreshing,
            IdleIconVisible: !motion,
            SpinnerVisible: motion,
            ProgressStripVisible: motion,
            RunAnimation: motion);
    }
}
