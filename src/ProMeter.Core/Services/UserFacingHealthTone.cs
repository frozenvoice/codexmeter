namespace ProMeter.Services;

/// <summary>
/// Maps a data-health kind to a semantic color tone. The Flyout UI resolves the tone to an
/// actual theme brush; this keeps the status-meaning-to-color policy out of code-behind.
/// </summary>
public enum StatusToneKind
{
    Ok,
    Accent,
    Danger,
    Muted
}

public static class UserFacingHealthTone
{
    public static StatusToneKind From(UserFacingHealthKind kind) => kind switch
    {
        UserFacingHealthKind.Usable => StatusToneKind.Ok,
        UserFacingHealthKind.Syncing => StatusToneKind.Accent,
        UserFacingHealthKind.NeedsConnection => StatusToneKind.Danger,
        UserFacingHealthKind.NeedsSignIn => StatusToneKind.Danger,
        UserFacingHealthKind.SyncFailed => StatusToneKind.Danger,
        UserFacingHealthKind.NeedsAttention => StatusToneKind.Danger,
        UserFacingHealthKind.Stale => StatusToneKind.Muted,
        _ => StatusToneKind.Muted
    };
}
