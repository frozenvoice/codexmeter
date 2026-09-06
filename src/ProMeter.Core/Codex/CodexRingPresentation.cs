using ProMeter.Services;

namespace ProMeter.Codex;

/// <summary>
/// Everything the Flyout Codex ring needs, computed once so the WPF code-behind only
/// assigns values instead of re-deriving Codex display policy.
/// </summary>
public sealed record CodexRingPresentation(
    double? UsedPercent,
    bool IsAvailable,
    bool IsDangerLevel,
    string CenterValueText,
    string CenterSubLabel,
    string BadgeText)
{
    public static CodexRingPresentation From(CodexQuotaSnapshot snapshot)
    {
        var window = snapshot.CompactWindow;
        var used = window?.UsedPercent;
        var clamped = used is double value ? Math.Clamp(value, 0, 100) : (double?)null;
        return new CodexRingPresentation(
            UsedPercent: clamped,
            IsAvailable: clamped is not null,
            IsDangerLevel: clamped is >= 100,
            CenterValueText: CodexDisplayFormatting.PercentText(used),
            CenterSubLabel: CodexDisplayFormatting.CompactWindowKindLabel(window),
            BadgeText: UiText.ServerBasedAccurateBadge);
    }
}
