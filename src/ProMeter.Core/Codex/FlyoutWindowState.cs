namespace ProMeter.Codex;

public static class FlyoutWindowState
{
    public static bool ShouldCloseOnDeactivate(bool pinned, bool closeOnDeactivateSetting) =>
        !pinned && closeOnDeactivateSetting;

    public static bool UseSavedPosition(bool pinned, bool positionConfigured) =>
        pinned && positionConfigured;

    public static bool RepositionNearAnchorOnUnpin => true;
}
