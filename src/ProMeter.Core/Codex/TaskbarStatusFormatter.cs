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
            TaskbarStripMode.Compact => $"{p}  {c}",
            _ => $"{p} {c}"
        };
    }

    public static string Tooltip(QuotaSnapshot chatgpt, CodexQuotaSnapshot codex)
    {
        var gpt = chatgpt.DisplayUsageUnavailable
            ? $"{UiText.GptPro} ?"
            : UiText.T(
                $"GPT Pro usage {chatgpt.Used}/{chatgpt.Limit}",
                $"GPT Pro 사용 {chatgpt.Used}/{chatgpt.Limit}");
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

    private static string ChatGptToken(QuotaSnapshot snapshot, TaskbarStripMode mode)
    {
        if (snapshot.DisplayUsageUnavailable)
        {
            return "P?";
        }

        return mode == TaskbarStripMode.Full
            ? $"P {snapshot.Used}/{snapshot.Limit}"
            : $"P{snapshot.Used}";
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
