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
    public async Task RunPumpsAsync_PipeEof_DoesNotHangOnCancellationIgnoringChromeInput()
    {
        using var chromeIn = new CancellationIgnoringHangStream();
        using var chromeOut = new MemoryStream();
        using var pipe = new MemoryStream();
        var started = DateTime.UtcNow;
        var result = await NativeMessagingHost.RunPumpsAsync(chromeIn, chromeOut, pipe, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CompanionHostLifecycleReason.PipeInputEof, result.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5));
        Assert.False(chromeIn.ReadCompleted);
    }

    [Fact]
    public async Task RunPumpsAsync_ChromeEof_DoesNotHangOnCancellationIgnoringPipeReader()
    {
        using var chromeIn = new MemoryStream();
        using var chromeOut = new MemoryStream();
        using var pipe = new CancellationIgnoringHangStream();
        var started = DateTime.UtcNow;
        var result = await NativeMessagingHost.RunPumpsAsync(chromeIn, chromeOut, pipe, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(CompanionHostLifecycleReason.ChromeInputEof, result.Reason);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1.5));
        Assert.False(pipe.ReadCompleted);
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
        Assert.Contains("OtherPumpDrainTimeout", host, StringComparison.Ordinal);
        Assert.DoesNotContain("await Task.WhenAll(chromeToPipe, pipeToChrome)", host, StringComparison.Ordinal);
        Assert.Contains("pipe.Connect(2000)", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.WriteLine", host, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Write(", host, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", host, StringComparison.Ordinal);
        Assert.Contains("ex.GetType().Name", host, StringComparison.Ordinal);
        var registration = File.ReadAllText(Find("src/ProMeter/Companion/CompanionRegistration.cs"));
        Assert.Contains("CompanionHostStager.EnsureStaged", registration, StringComparison.Ordinal);
        Assert.Contains("EnsureCurrent", registration, StringComparison.Ordinal);
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("EnsureCompanionHostRegistration", app, StringComparison.Ordinal);
        var ensure = app.IndexOf("EnsureCompanionHostRegistration();", StringComparison.Ordinal);
        var start = app.IndexOf("_companionServer.Start();", StringComparison.Ordinal);
        Assert.True(ensure >= 0 && start > ensure);
        var exitApp = app.IndexOf("private void ExitApp()", StringComparison.Ordinal);
        Assert.True(exitApp >= 0);
        var stop = app.IndexOf("StopAsync(CompanionPipeServer.StopTimeout)", exitApp, StringComparison.Ordinal);
        var shutdown = app.IndexOf("Shutdown();", exitApp, StringComparison.Ordinal);
        Assert.True(stop >= 0 && shutdown > stop);
    }

    [Fact]
    public void BuiltInExtensionId_IsWiredIntoStartupRegistrationForBothBrowsers()
    {
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("ResolveBuiltInCompanionExtensionId", app, StringComparison.Ordinal);
        Assert.Contains("CompanionExtensionManifest.TryReadBuiltInExtensionId", app, StringComparison.Ordinal);

        var ensureMethod = app.IndexOf("private void EnsureCompanionHostRegistration()", StringComparison.Ordinal);
        Assert.True(ensureMethod >= 0);
        var builtInUsedInEnsure = app.IndexOf("builtInId", ensureMethod, StringComparison.Ordinal);
        var ensureCurrentCall = app.IndexOf("CompanionRegistration.EnsureCurrent(exe, chromeId, edgeId, builtInId)", ensureMethod, StringComparison.Ordinal);
        Assert.True(builtInUsedInEnsure >= 0 && ensureCurrentCall > builtInUsedInEnsure);

        // Only compute/use the built-in ID when the user has actually opted into
        // Browser Companion, never unconditionally at every startup.
        var optInCheck = app.IndexOf("_settings.CompanionConnectOptIn ? ResolveBuiltInCompanionExtensionId()", ensureMethod, StringComparison.Ordinal);
        Assert.True(optInCheck >= 0 && optInCheck < ensureCurrentCall);

        var registration = File.ReadAllText(Find("src/ProMeter/Companion/CompanionRegistration.cs"));
        Assert.Contains("builtInExtensionId", registration, StringComparison.Ordinal);

        // The deterministic ID applies to both browsers, so its presence alone must be
        // enough to register both HKCU NativeMessagingHosts entries.
        var chromeSetHost = registration.IndexOf("SetHost(Registry.CurrentUser, ChromeNativeHosts,", StringComparison.Ordinal);
        var edgeSetHost = registration.IndexOf("SetHost(Registry.CurrentUser, EdgeNativeHosts,", StringComparison.Ordinal);
        Assert.True(chromeSetHost >= 0 && edgeSetHost >= 0);
        var chromeGuard = registration.LastIndexOf("if (", chromeSetHost, StringComparison.Ordinal);
        var edgeGuard = registration.LastIndexOf("if (", edgeSetHost, StringComparison.Ordinal);
        Assert.Contains("builtInExtensionId", registration[chromeGuard..chromeSetHost], StringComparison.Ordinal);
        Assert.Contains("builtInExtensionId", registration[edgeGuard..edgeSetHost], StringComparison.Ordinal);

        var manifest = File.ReadAllText(Find("src/ProMeter.Core/Companion/CompanionHostManifest.cs"));
        Assert.Contains("builtInExtensionId", manifest, StringComparison.Ordinal);

        // dev-run.ps1 must not duplicate registry logic: it may read the HKCU
        // NativeMessagingHosts entry ProMeter itself owns, but must never write one.
        var devRun = File.ReadAllText(Find("dev-run.ps1"));
        Assert.DoesNotContain("Set-ItemProperty", devRun, StringComparison.Ordinal);
        Assert.DoesNotContain("New-ItemProperty", devRun, StringComparison.Ordinal);
        Assert.DoesNotContain("New-Item -Path 'HKCU", devRun, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove-Item -Path 'HKCU", devRun, StringComparison.Ordinal);
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

    private sealed class CancellationIgnoringHangStream : Stream
    {
        private readonly TaskCompletionSource<int> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool ReadCompleted => _read.Task.IsCompleted;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _read.Task.GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _read.Task;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            new(_read.Task);

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
