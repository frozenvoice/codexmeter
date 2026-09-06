namespace ProMeter.Codex;

public static class FlyoutWindowState
{
    public static bool HidesOnDeactivate => false;

    public static bool IsTopmost(bool pinned) => pinned;

    public static bool AllowsHeaderDrag => true;

    public static bool UseSavedPosition(bool positionConfigured) => positionConfigured;

    public static bool RepositionNearAnchorOnUnpin => false;
}
