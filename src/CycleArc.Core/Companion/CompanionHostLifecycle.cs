using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Companion;

public enum CompanionHostLifecycleReason
{
    HostStarted,
    PipeConnected,
    ChromeInputEof,
    PipeInputEof,
    ChromePumpFailed,
    PipePumpFailed,
    OriginRejected,
    HostFailed,
    Cancelled,
    PipeServerAccepted,
    PipeServerEof
}

public readonly record struct CompanionHostPumpResult(
    CompanionHostLifecycleReason Reason,
    string? ExceptionType);

public static class CompanionHostLifecycle
{
    public const string MessageType = "hostLifecycle";

    public static Action<string>? LogSink { get; set; }

    public static string ToWire(CompanionHostLifecycleReason reason) => reason switch
    {
        CompanionHostLifecycleReason.HostStarted => "host-started",
        CompanionHostLifecycleReason.PipeConnected => "pipe-connected",
        CompanionHostLifecycleReason.ChromeInputEof => "chrome-input-eof",
        CompanionHostLifecycleReason.PipeInputEof => "pipe-input-eof",
        CompanionHostLifecycleReason.ChromePumpFailed => "chrome-pump-failed",
        CompanionHostLifecycleReason.PipePumpFailed => "pipe-pump-failed",
        CompanionHostLifecycleReason.OriginRejected => "origin-rejected",
        CompanionHostLifecycleReason.HostFailed => "host-failed",
        CompanionHostLifecycleReason.Cancelled => "cancelled",
        CompanionHostLifecycleReason.PipeServerAccepted => "pipe-server-accepted",
        CompanionHostLifecycleReason.PipeServerEof => "pipe-server-eof",
        _ => "host-failed"
    };

    public static bool TryParseWire(string? value, out CompanionHostLifecycleReason reason)
    {
        reason = CompanionHostLifecycleReason.HostFailed;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var candidate in Enum.GetValues<CompanionHostLifecycleReason>())
        {
            if (string.Equals(ToWire(candidate), value.Trim(), StringComparison.Ordinal))
            {
                reason = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool IsMessage(CompanionBridgeMessage? message) =>
        message is not null
        && string.Equals(message.Type, MessageType, StringComparison.OrdinalIgnoreCase);

    public static bool TryGetReason(CompanionBridgeMessage? message, out string reason)
    {
        reason = "";
        if (!IsMessage(message) || !TryParseWire(message!.Reason, out var parsed))
        {
            return false;
        }

        reason = ToWire(parsed);
        return true;
    }

    public static CompanionBridgeMessage CreateMessage(CompanionHostLifecycleReason reason) =>
        new()
        {
            Type = MessageType,
            Reason = ToWire(reason)
        };

    public static string Serialize(CompanionHostLifecycleReason reason) =>
        CompanionBridgeProtocol.Serialize(CreateMessage(reason));

    public static CompanionHostLifecycleReason ClassifyPumpCompletion(
        bool chromeCompletedFirst,
        bool completedCanceled,
        bool completedFaulted,
        CompanionHostLifecycleReason? completedReason,
        bool callerCanceled)
    {
        if (callerCanceled && completedCanceled)
        {
            return CompanionHostLifecycleReason.Cancelled;
        }

        if (completedFaulted)
        {
            return chromeCompletedFirst
                ? CompanionHostLifecycleReason.ChromePumpFailed
                : CompanionHostLifecycleReason.PipePumpFailed;
        }

        if (completedReason is { } reason)
        {
            return reason;
        }

        return chromeCompletedFirst
            ? CompanionHostLifecycleReason.ChromeInputEof
            : CompanionHostLifecycleReason.PipeInputEof;
    }

    public static bool ShouldEmitToPipe(CompanionHostLifecycleReason reason) =>
        reason is CompanionHostLifecycleReason.ChromeInputEof
            or CompanionHostLifecycleReason.ChromePumpFailed;

    public static string StartedLine() => "native host started";

    public static string PipeConnectedLine() => "native host pipe-connected";

    public static string ExitingLine(CompanionHostLifecycleReason reason, string? exceptionType = null)
    {
        var line = $"native host exiting reason={ToWire(reason)}";
        if (string.IsNullOrWhiteSpace(exceptionType))
        {
            return line;
        }

        return $"{line} type={SanitizeTypeName(exceptionType)}";
    }

    public static string PipeAcceptedLine(int generation) =>
        $"companion pipe accepted generation={generation}";

    public static string PipeEofLine(int generation) =>
        $"companion pipe eof generation={generation}";

    public static string PipeClosedLine(int generation, string reason) =>
        $"companion pipe closed generation={generation} reason={reason}";

    public static string LifecycleReceivedLine(int generation, string reason) =>
        $"native host lifecycle reason={reason} generation={generation}";

    public static void Write(string line)
    {
        try
        {
            var sink = LogSink;
            if (sink is not null)
            {
                sink(line);
                return;
            }

            new AppLog().Info(line);
        }
        catch
        {
        }
    }

    public static string SanitizeTypeName(string? exceptionType)
    {
        if (string.IsNullOrWhiteSpace(exceptionType))
        {
            return "Exception";
        }

        var name = exceptionType.Trim();
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '.' or '_')
            {
                continue;
            }

            return "Exception";
        }

        return name.Length > 80 ? name[..80] : name;
    }
}
