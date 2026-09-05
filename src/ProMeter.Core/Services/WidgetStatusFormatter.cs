using ProMeter.Codex;

namespace ProMeter.Services;

public static class WidgetStatusFormatter
{
    public static string ProLine(ProStatusPresentation presentation) =>
        $"{UiText.GptPro}   {presentation.ProStateText}";

    public static string ResetLine(ProStatusPresentation presentation)
    {
        if (presentation.HasServerReset && presentation.ServerResetAt is { } reset)
        {
            return reset.ToLocalTime().ToString("M/d HH:mm", CultureInfo.InvariantCulture);
        }

        return presentation.ResetAmbiguous ? UiText.MultipleProResets : "";
    }

    public static string CodexLine(CodexQuotaSnapshot snapshot)
    {
        var status = CodexDisplayFormatting.StatusText(snapshot);
        if (snapshot.CompactWindow?.UsedPercent is { } percent)
        {
            var kind = CodexDisplayFormatting.CompactWindowKindLabel(snapshot.CompactWindow);
            return $"Codex {kind}   {CodexDisplayFormatting.PercentText(percent)}";
        }

        return string.IsNullOrWhiteSpace(status) ? $"Codex {UiText.CodexUnavailable}" : $"Codex {status}";
    }

    public static string HistoryLine(ProStatusPresentation presentation, QuotaSnapshot snapshot) =>
        $"{UiText.T("History", "기록")} {presentation.ReconstructedText} · XH {snapshot.Reasoning.ExtraHigh.ToString(CultureInfo.InvariantCulture)}";
}
