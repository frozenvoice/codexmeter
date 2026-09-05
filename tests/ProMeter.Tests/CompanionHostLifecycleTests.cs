using System.IO.Pipes;
using System.Text;
using ProMeter.Companion;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class CompanionHostLifecycleTests
{
    [Fact]
    public void WireReasons_AreFixedAndRejectUnknown()
    {
        Assert.Equal("chrome-input-eof", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.ChromeInputEof));
        Assert.Equal("pipe-input-eof", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipeInputEof));
        Assert.Equal("chrome-pump-failed", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.ChromePumpFailed));
        Assert.Equal("pipe-pump-failed", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipePumpFailed));
        Assert.Equal("host-started", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.HostStarted));
        Assert.Equal("pipe-connected", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipeConnected));
        Assert.Equal("pipe-server-accepted", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipeServerAccepted));
        Assert.Equal("pipe-server-eof", CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipeServerEof));
        Assert.True(CompanionHostLifecycle.TryParseWire("chrome-input-eof", out var parsed));
        Assert.Equal(CompanionHostLifecycleReason.ChromeInputEof, parsed);
        Assert.False(CompanionHostLifecycle.TryParseWire("accountId=secret", out _));
        Assert.False(CompanionHostLifecycle.TryParseWire("{\"body\":\"prompt\"}", out _));
        Assert.False(CompanionHostLifecycle.TryParseWire("Authorization", out _));
    }

    [Fact]
    public void ClassifyPumpCompletion_DistinguishesSides()
    {
        Assert.Equal(
            CompanionHostLifecycleReason.ChromeInputEof,
            CompanionHostLifecycle.ClassifyPumpCompletion(true, false, false, CompanionHostLifecycleReason.ChromeInputEof, false));
        Assert.Equal(
            CompanionHostLifecycleReason.PipeInputEof,
            CompanionHostLifecycle.ClassifyPumpCompletion(false, false, false, CompanionHostLifecycleReason.PipeInputEof, false));
        Assert.Equal(
            CompanionHostLifecycleReason.ChromePumpFailed,
            CompanionHostLifecycle.ClassifyPumpCompletion(true, false, true, null, false));
        Assert.Equal(
            CompanionHostLifecycleReason.PipePumpFailed,
            CompanionHostLifecycle.ClassifyPumpCompletion(false, false, true, null, false));
        Assert.Equal(
            CompanionHostLifecycleReason.Cancelled,
            CompanionHostLifecycle.ClassifyPumpCompletion(true, true, false, null, true));
        Assert.True(CompanionHostLifecycle.ShouldEmitToPipe(CompanionHostLifecycleReason.ChromeInputEof));
        Assert.False(CompanionHostLifecycle.ShouldEmitToPipe(CompanionHostLifecycleReason.PipeInputEof));
    }

    [Fact]
    public void LifecycleMessage_HasNoSecretsOrRawPayload()
    {
        var json = CompanionHostLifecycle.Serialize(CompanionHostLifecycleReason.ChromeInputEof);
        Assert.Contains("\"type\":\"hostLifecycle\"", json.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chrome-input-eof", json, StringComparison.Ordinal);
        Assert.DoesNotContain("pairingToken", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", json, StringComparison.OrdinalIgnoreCase);
        var parsed = CompanionBridgeProtocol.Parse(json);
        Assert.True(parsed.Accepted);
        Assert.True(CompanionHostLifecycle.TryGetReason(parsed.Message, out var reason));
        Assert.Equal("chrome-input-eof", reason);
        var forged = CompanionBridgeProtocol.Parse(
            """{"type":"hostLifecycle","reason":"chrome-input-eof","body":"{\"accountId\":\"must-not-log\",\"email\":\"a@b.c\"}"}""");
        Assert.True(CompanionHostLifecycle.TryGetReason(forged.Message, out var stillSafe));
        Assert.Equal("chrome-input-eof", stillSafe);
        var line = CompanionHostLifecycle.LifecycleReceivedLine(12, stillSafe);
        Assert.Equal("native host lifecycle reason=chrome-input-eof generation=12", line);
        Assert.DoesNotContain("accountId", line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("must-not-log", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitingLine_LogsTypeNotExceptionBody()
    {
        var line = CompanionHostLifecycle.ExitingLine(CompanionHostLifecycleReason.ChromePumpFailed, "IOException");
        Assert.Equal("native host exiting reason=chrome-pump-failed type=IOException", line);
        Assert.Equal("Exception", CompanionHostLifecycle.SanitizeTypeName("IOException: secret token"));
        Assert.Equal("Exception", CompanionHostLifecycle.SanitizeTypeName("{\"accountId\":\"x\"}"));
        Assert.DoesNotContain("secret", CompanionHostLifecycle.ExitingLine(
            CompanionHostLifecycleReason.HostFailed,
            CompanionHostLifecycle.SanitizeTypeName("TimeoutException: Authorization Bearer abc")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChromeInputEof_EmitsLifecycleToPipeNotStdout()
    {
        var pipeName = "ProMeterLifecycleTest-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync();
        await client.ConnectAsync(2000);
        await accepted;

        using var chromeIn = new MemoryStream();
        using var chromeOut = new MemoryStream();
        var pumps = NativeMessagingHost.RunPumpsAsync(chromeIn, chromeOut, client, CancellationToken.None);
        using var readCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var framed = await NativeMessagingFraming.ReadMessageAsync(
            server,
            CompanionBridgeProtocol.MaxNativeMessageBytes,
            readCts.Token);
        var result = await pumps;
        Assert.Equal(CompanionHostLifecycleReason.ChromeInputEof, result.Reason);
        Assert.NotNull(framed);
        Assert.Contains("hostLifecycle", framed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chrome-input-eof", framed, StringComparison.Ordinal);
        Assert.DoesNotContain("pairingToken", framed, StringComparison.OrdinalIgnoreCase);
        var stdout = Encoding.UTF8.GetString(chromeOut.ToArray());
        Assert.DoesNotContain("native host", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("INFO", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome-input-eof", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PipeServerSource_LogsFixedReasonsWithoutRawPayloads()
    {
        var source = File.ReadAllText(Find("src/ProMeter.Core/Companion/CompanionPipeServer.cs"));
        Assert.Contains("CompanionHostLifecycle.PipeAcceptedLine", source, StringComparison.Ordinal);
        Assert.Contains("CompanionHostLifecycle.PipeEofLine", source, StringComparison.Ordinal);
        Assert.Contains("CompanionHostLifecycle.PipeClosedLine", source, StringComparison.Ordinal);
        Assert.Contains("CompanionHostLifecycle.TryGetReason", source, StringComparison.Ordinal);
        Assert.DoesNotContain("message.Body", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_log.Info(raw", source, StringComparison.Ordinal);
        var host = File.ReadAllText(Find("src/ProMeter.Core/Companion/NativeMessagingHost.cs"));
        Assert.Contains("CompanionHostLifecycle.StartedLine", host, StringComparison.Ordinal);
        Assert.Contains("CompanionHostLifecycle.PipeConnectedLine", host, StringComparison.Ordinal);
        Assert.Contains("CompanionHostLifecycle.ExitingLine", host, StringComparison.Ordinal);
        Assert.Contains("TryEmitLifecycleToPipeAsync", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.WriteLine", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Write(", host, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", host, StringComparison.Ordinal);
        Assert.Contains("ex.GetType().Name", host, StringComparison.Ordinal);
    }

    [Fact]
    public void OriginRejected_DoesNotWriteStdout()
    {
        var logs = new List<string>();
        var previous = CompanionHostLifecycle.LogSink;
        CompanionHostLifecycle.LogSink = logs.Add;
        try
        {
            NativeMessagingHost.Run([]);
            NativeMessagingHost.Run(["--parent-window=1234"]);
        }
        finally
        {
            CompanionHostLifecycle.LogSink = previous;
        }

        Assert.Contains(logs, line => line.Contains("reason=origin-rejected", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("pairing", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(logs, line => line.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
