using System.Globalization;
using ProMeter.Models;
using ProMeter.Services;

namespace ProMeter.Codex;

public static class TaskbarStatusFormatter
{
    public static string Format(QuotaSnapshot chatgpt, CodexQuotaSnapshot codex, TaskbarStripMode mode)
    {
        var p = ChatGptToken(chatgpt, mode);
        var c = CodexToken(codex, mode);
        return mode switch
        {
            TaskbarStripMode.Full => $"{p} · {c}",
            TaskbarStripMode.Compact => $"{p} {c}",
            _ => $"{p} {c}"
        };
    }

    public static string Tooltip(QuotaSnapshot chatgpt, CodexQuotaSnapshot codex)
    {
        var presentation = ProStatusPresentation.From(chatgpt);
        var gpt = string.Join(
            Environment.NewLine,
            new[]
            {
                $"{UiText.GptPro}: {presentation.ProStateText}",
                presentation.HasServerReset ? $"{UiText.ServerReset}: {presentation.ResetText}" : null,
                $"{UiText.ExactRemaining}: {presentation.ExactRemainingText}"
            }.Where(line => !string.IsNullOrWhiteSpace(line)));
        var window = codex.CompactWindow;
        string codexLine;
        if (codex.Status == CodexQuotaStatus.Refreshing && window?.UsedPercent is null)
        {
            codexLine = UiText.CodexRefreshing;
        }
        else if (window?.UsedPercent is { } percent)
        {
            codexLine = $"{CodexDisplayFormatting.CompactWindowKindLabel(window)} {CodexDisplayFormatting.PercentText(percent)}";
        }
        else
        {
            codexLine = StatusOrUnavailable(codex);
        }

        var checkedAt = codex.LastSuccessfulRefresh is { } success
            ? $"{UiText.T("Codex last checked", "Codex 마지막 확인")} {CodexDisplayFormatting.TimeOfDay(success)}"
            : $"{UiText.T("Codex last checked", "Codex 마지막 확인")} {UiText.Never}";
        var stale = codex.Status == CodexQuotaStatus.Stale ? UiText.CodexDataStale : null;
        return string.Join(Environment.NewLine, new[] { gpt, codexLine, checkedAt, stale }.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    public static int EstimatedWidthDip(TaskbarStripMode mode) => mode switch
    {
        TaskbarStripMode.Full => TaskbarStatusPositioner.FullWidthDip,
        TaskbarStripMode.Compact => TaskbarStatusPositioner.CompactWidthDip,
        _ => TaskbarStatusPositioner.UltraWidthDip
    };

    public static string ChatGptToken(QuotaSnapshot snapshot, TaskbarStripMode mode)
    {
        var presentation = ProStatusPresentation.From(snapshot);
        var stale = presentation.Stale && presentation.ServerStatusKnown ? "~" : "";
        var count = CurrentCycleReconstructedToken(snapshot);
        if (count is not null && !presentation.ExactRemainingAvailable)
        {
            var prefix = !presentation.ServerStatusKnown || presentation.ResetAmbiguous
                ? "P?"
                : presentation.Restricted
                    ? "P!"
                    : "P";
            var staleMark = count.EndsWith('~') && stale == "~" ? "" : stale;
            return mode == TaskbarStripMode.Full
                ? $"{prefix} {count}{staleMark}"
                : $"{prefix}{count}{staleMark}";
        }

        if (!presentation.ServerStatusKnown || presentation.ResetAmbiguous)
        {
            return "P?";
        }

        if (presentation.Restricted)
        {
            if (presentation.HasServerReset && mode != TaskbarStripMode.UltraCompact)
            {
                return mode == TaskbarStripMode.Full
                    ? $"P! {presentation.ResetCompactTime}{stale}"
                    : $"P!{presentation.ResetCompactTime}{stale}";
            }

            return $"P!{stale}";
        }

        return mode == TaskbarStripMode.Full ? $"P OK{stale}" : $"POK{stale}";
    }

    private static string? CurrentCycleReconstructedToken(QuotaSnapshot snapshot)
    {
        if (snapshot.DisplayUsageUnavailable)
        {
            return null;
        }

        if (snapshot.ReconstructedUsed <= 0 && snapshot.LastSync is null && snapshot.Used <= 0)
        {
            return null;
        }

        return ProStatusPresentation.CompactReconstructedToken(snapshot);
    }

    private static string CodexToken(CodexQuotaSnapshot snapshot, TaskbarStripMode mode)
    {
        var staleMark = snapshot.Status == CodexQuotaStatus.Stale ? (mode == TaskbarStripMode.Full ? "·" : "!") : "";
        if (snapshot.Status == CodexQuotaStatus.Refreshing && snapshot.CompactWindow?.UsedPercent is null)
        {
            return mode == TaskbarStripMode.Full ? "C …" : "C…";
        }

        if (snapshot.CompactWindow?.UsedPercent is not { } percent)
        {
            return "C?";
        }

        var rounded = Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero)
            .ToString(CultureInfo.InvariantCulture);
        return mode switch
        {
            TaskbarStripMode.Full => $"C {rounded}%{staleMark}",
            TaskbarStripMode.Compact => $"C{rounded}%{staleMark}",
            _ => $"C{rounded}{staleMark}"
        };
    }

    private static string StatusOrUnavailable(CodexQuotaSnapshot snapshot)
    {
        var status = CodexDisplayFormatting.StatusText(snapshot);
        return string.IsNullOrWhiteSpace(status) ? UiText.CodexUnavailable : status;
    }
}
