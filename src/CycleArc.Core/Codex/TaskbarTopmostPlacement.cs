namespace CycleArc.Codex;

public static class TaskbarTopmostPlacement
{
    public static readonly IntPtr HwndTopmost = new(-1);
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpNoOwnerZOrder = 0x0200;

    public const uint Flags = SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder | SwpShowWindow;

    public static bool HasRequiredSafetyFlags =>
        (Flags & SwpNoActivate) == SwpNoActivate
        && (Flags & SwpNoMove) == SwpNoMove
        && (Flags & SwpNoSize) == SwpNoSize
        && (Flags & SwpNoOwnerZOrder) == SwpNoOwnerZOrder
        && (Flags & SwpShowWindow) == SwpShowWindow
        && (Flags & SwpNoZOrder) == 0;

    public static bool ShouldReassert(bool overlayLogicallyVisible) => overlayLogicallyVisible;
}
