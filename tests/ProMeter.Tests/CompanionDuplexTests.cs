using System.IO.Pipes;
using System.Text;
using ProMeter.Companion;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class CompanionDuplexTests
{
    [Fact]
    public async Task HelloThenAppInitiatedInvoke_DoesNotRequireAnotherExtensionMessage()
    {
        var pairing = new CompanionPairingState { Token = "pairing-token-for-duplex-tests-aa" };
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var pipeName = "ProMeterCompanionTest-" + Guid.NewGuid().ToString("N");
        using var server = new CompanionPipeServer(hub, pairing, new AppLog(Path.Combine(Path.GetTempPath(), pipeName)), pipeName);
        server.Start();
        await Task.Delay(50);

        using var chromeInServer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var chromeInClient = new AnonymousPipeClientStream(PipeDirection.In, chromeInServer.GetClientHandleAsString());
        using var chromeOutServer = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None);
        using var chromeOutClient = new AnonymousPipeClientStream(PipeDirection.Out, chromeOutServer.GetClientHandleAsString());
        using var named = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await named.ConnectAsync(2000);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var pumps = NativeMessagingHost.RunPumpsAsync(chromeInClient, chromeOutClient, named, cts.Token);

        await WriteFramed(chromeInServer, """{"type":"hello","pairingToken":"pairing-token-for-duplex-tests-aa"}""");
        var helloAck = await ReadFramed(chromeOutServer, cts.Token);
        Assert.Contains("helloAck", helloAck, StringComparison.OrdinalIgnoreCase);

        var invokeTask = hub.RequestAsync(CompanionOperation.GetSessionStatus, new CompanionOperationArgs(), CancellationToken.None);
        var invoke = await ReadFramed(chromeOutServer, cts.Token);
        Assert.Contains("\"type\":\"invoke\"", invoke.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetSessionStatus", invoke, StringComparison.Ordinal);

        var parsed = CompanionBridgeProtocol.Parse(invoke);
        Assert.True(parsed.Accepted);
        await WriteFramed(chromeInServer, CompanionBridgeProtocol.Serialize(new CompanionBridgeMessage
        {
            Type = CompanionBridgeProtocol.InvokeResult,
            RequestId = parsed.Message!.RequestId,
            Operation = "GetSessionStatus",
            PairingToken = pairing.Token,
            Status = 200,
            Body = """{"signedIn":true,"user":{"id":"u1"}}"""
        }));

        var response = await invokeTask;
        Assert.True(response.IsSuccess);
        Assert.Contains("u1", response.Body, StringComparison.Ordinal);

        var second = hub.RequestAsync(CompanionOperation.GetModels, new CompanionOperationArgs(), CancellationToken.None);
        var secondInvoke = await ReadFramed(chromeOutServer, cts.Token);
        var secondParsed = CompanionBridgeProtocol.Parse(secondInvoke);
        await WriteFramed(chromeInServer, CompanionBridgeProtocol.Serialize(new CompanionBridgeMessage
        {
            Type = CompanionBridgeProtocol.InvokeResult,
            RequestId = secondParsed.Message!.RequestId,
            Operation = "GetModels",
            PairingToken = pairing.Token,
            Status = 200,
            Body = """{"models":[{"slug":"gpt-6-pro"}]}"""
        }));
        Assert.True((await second).IsSuccess);

        var firstConcurrent = hub.RequestAsync(CompanionOperation.GetAccountMe, new CompanionOperationArgs(), CancellationToken.None);
        var secondConcurrent = hub.RequestAsync(CompanionOperation.GetAccountCheck, new CompanionOperationArgs(), CancellationToken.None);
        var concurrentA = await ReadFramed(chromeOutServer, cts.Token);
        var concurrentB = await ReadFramed(chromeOutServer, cts.Token);
        foreach (var frame in new[] { concurrentA, concurrentB })
        {
            var parsedFrame = CompanionBridgeProtocol.Parse(frame);
            Assert.True(parsedFrame.Accepted);
            Assert.StartsWith("{", frame, StringComparison.Ordinal);
            var operation = parsedFrame.Message!.Operation;
            var body = string.Equals(operation, "GetAccountCheck", StringComparison.Ordinal)
                ? """{"accounts":{}}"""
                : """{"id":"u1"}""";
            await WriteFramed(chromeInServer, CompanionBridgeProtocol.Serialize(new CompanionBridgeMessage
            {
                Type = CompanionBridgeProtocol.InvokeResult,
                RequestId = parsedFrame.Message!.RequestId,
                Operation = operation,
                PairingToken = pairing.Token,
                Status = 200,
                Body = body
            }));
        }

        Assert.True((await firstConcurrent).IsSuccess);
        Assert.True((await secondConcurrent).IsSuccess);

        cts.Cancel();
        try { await pumps; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task Timeout_RemovesPendingRequest()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromMilliseconds(80) };
        hub.BeginConnection();
        hub.TryAcceptHello(hub.ConnectionGeneration, true);
        var response = await hub.RequestAsync(CompanionOperation.GetModels, new CompanionOperationArgs(), CancellationToken.None);
        Assert.Equal("bridge request timed out", response.Error);
        Assert.False(response.SchemaMismatch);
    }

    [Fact]
    public async Task Disconnect_FailsPendingRequests()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var pending = hub.RequestAsync(CompanionOperation.GetModels, new CompanionOperationArgs(), CancellationToken.None);
        await Task.Delay(20);
        hub.Disconnect(generation);
        var response = await pending;
        Assert.Contains("disconnected", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StaleGeneration_IsIgnored()
    {
        var hub = new CompanionRequestHub { RequestTimeout = TimeSpan.FromSeconds(5) };
        string? requestId = null;
        hub.Outgoing += message => requestId = message.RequestId;
        var generation = hub.BeginConnection();
        hub.TryAcceptHello(generation, true);
        var pending = hub.RequestAsync(CompanionOperation.GetModels, new CompanionOperationArgs(), CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(requestId));
        Assert.False(hub.TryComplete(requestId!, new ProviderResponse { Status = 200, Body = """{"stale":true}""" }, generation - 1));
        Assert.False(pending.IsCompleted);
        Assert.True(hub.TryComplete(requestId!, new ProviderResponse { Status = 200, Body = """{"models":[]}""" }, generation));
        Assert.True((await pending).IsSuccess);
    }

    [Fact]
    public async Task ConcurrentWrites_DoNotCorruptFraming()
    {
        using var stream = new MemoryStream();
        var writeLock = new SemaphoreSlim(1, 1);
        var first = NativeMessagingFraming.WriteMessageAsync(stream, """{"type":"invoke","requestId":"a"}""", writeLock, CancellationToken.None);
        var second = NativeMessagingFraming.WriteMessageAsync(stream, """{"type":"invoke","requestId":"b"}""", writeLock, CancellationToken.None);
        await Task.WhenAll(first, second);
        stream.Position = 0;
        var one = await NativeMessagingFraming.ReadMessageAsync(stream, CompanionBridgeProtocol.MaxNativeMessageBytes, CancellationToken.None);
        var two = await NativeMessagingFraming.ReadMessageAsync(stream, CompanionBridgeProtocol.MaxNativeMessageBytes, CancellationToken.None);
        Assert.Contains("invoke", one, StringComparison.Ordinal);
        Assert.Contains("invoke", two, StringComparison.Ordinal);
        Assert.Contains("\"requestId\":\"a\"", one + two, StringComparison.Ordinal);
        Assert.Contains("\"requestId\":\"b\"", one + two, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedFrame_ReturnsPayloadTooLarge()
    {
        using var stream = new MemoryStream();
        var huge = new string('x', CompanionBridgeProtocol.MaxNativeMessageBytes + 10);
        var payload = Encoding.UTF8.GetBytes(huge);
        await stream.WriteAsync(BitConverter.GetBytes(payload.Length));
        await stream.WriteAsync(payload);
        stream.Position = 0;
        var message = await NativeMessagingFraming.ReadMessageAsync(stream, CompanionBridgeProtocol.MaxNativeMessageBytes, CancellationToken.None);
        Assert.NotNull(message);
        Assert.Contains("PayloadTooLarge", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Framing_DoesNotWriteBareText()
    {
        using var stream = new MemoryStream();
        var writeLock = new SemaphoreSlim(1, 1);
        await NativeMessagingFraming.WriteMessageAsync(stream, """{"type":"helloAck","accepted":true}""", writeLock, CancellationToken.None);
        var bytes = stream.ToArray();
        Assert.True(bytes.Length > 4);
        Assert.Equal(BitConverter.ToInt32(bytes, 0), bytes.Length - 4);
        var body = Encoding.UTF8.GetString(bytes.AsSpan(4));
        Assert.StartsWith("{", body, StringComparison.Ordinal);
        Assert.DoesNotContain("INFO", body, StringComparison.Ordinal);
    }

    private static async Task WriteFramed(Stream stream, string json)
    {
        var writeLock = new SemaphoreSlim(1, 1);
        await NativeMessagingFraming.WriteMessageAsync(stream, json, writeLock, CancellationToken.None);
    }

    private static async Task<string> ReadFramed(Stream stream, CancellationToken cancellationToken)
    {
        var message = await NativeMessagingFraming.ReadMessageAsync(stream, CompanionBridgeProtocol.MaxNativeMessageBytes, cancellationToken);
        Assert.NotNull(message);
        return message!;
    }
}
