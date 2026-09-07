using System.Diagnostics;
using System.IO.Pipes;
using CodexMeter.Companion;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class CompanionPipeServerShutdownTests
{
    [Fact]
    public async Task StopAsync_NoClient_ReturnsPromptly()
    {
        using var server = CreateServer(out var hub, out _);
        server.Start();
        await Task.Delay(50);
        var started = Stopwatch.StartNew();
        await server.StopAsync(TimeSpan.FromMilliseconds(800));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(hub.IsConnected);
    }

    [Fact]
    public async Task StopAsync_ConnectedClient_ObservesDisconnect()
    {
        var pipeName = UniquePipe();
        using var server = CreateServer(pipeName, out _, out var pairing);
        server.Start();
        await Task.Delay(50);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await WriteFramed(client, $$"""{"type":"hello","pairingToken":"{{pairing.Token}}"}""");
        using var ackCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ack = await ReadFramed(client, ackCts.Token);
        Assert.Contains("helloAck", ack, StringComparison.OrdinalIgnoreCase);

        var read = NativeMessagingFraming.ReadMessageAsync(client, CompanionBridgeProtocol.MaxNativeMessageBytes, CancellationToken.None);
        await server.StopAsync(TimeSpan.FromMilliseconds(800));
        string? leftover = null;
        try
        {
            leftover = await read.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
        }

        Assert.Null(leftover);
    }

    [Fact]
    public async Task StopAsync_PendingRequest_CompletesAsDisconnected()
    {
        var pipeName = UniquePipe();
        using var server = CreateServer(pipeName, TimeSpan.FromSeconds(8), out var hub, out var pairing);
        server.Start();
        await Task.Delay(50);
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(2000);
        await WriteFramed(client, $$"""{"type":"hello","pairingToken":"{{pairing.Token}}"}""");
        using var ackCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var ack = await ReadFramed(client, ackCts.Token);
        Assert.Contains("helloAck", ack, StringComparison.OrdinalIgnoreCase);
        Assert.True(hub.IsConnected);

        var pending = hub.RequestAsync(CompanionOperation.GetModels, new CompanionOperationArgs(), CancellationToken.None);
        using var invokeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = await ReadFramed(client, invokeCts.Token);
        await server.StopAsync(TimeSpan.FromMilliseconds(800));
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(response.IsCompanionDisconnected);
        Assert.Contains("disconnected", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopAsync_AndDispose_AreIdempotent()
    {
        using var server = CreateServer(out _, out _);
        server.Start();
        await Task.Delay(30);
        await server.StopAsync();
        await server.StopAsync();
        server.Dispose();
        server.Dispose();
    }

    [Fact]
    public async Task StopAsync_DoesNotWaitIndefinitely()
    {
        using var server = CreateServer(out _, out _);
        server.Start();
        await Task.Delay(30);
        var started = Stopwatch.StartNew();
        await server.StopAsync(TimeSpan.FromMilliseconds(200));
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(900));
    }

    private static CompanionPipeServer CreateServer(out CompanionRequestHub hub, out CompanionPairingState pairing) =>
        CreateServer(UniquePipe(), TimeSpan.FromSeconds(5), out hub, out pairing);

    private static CompanionPipeServer CreateServer(string pipeName, out CompanionRequestHub hub, out CompanionPairingState pairing) =>
        CreateServer(pipeName, TimeSpan.FromSeconds(5), out hub, out pairing);

    private static CompanionPipeServer CreateServer(string pipeName, TimeSpan requestTimeout, out CompanionRequestHub hub, out CompanionPairingState pairing)
    {
        pairing = new CompanionPairingState { Token = "pairing-token-for-shutdown-tests-aa" };
        hub = new CompanionRequestHub { RequestTimeout = requestTimeout };
        return new CompanionPipeServer(hub, pairing, new AppLog(Path.Combine(Path.GetTempPath(), pipeName)), pipeName);
    }

    private static string UniquePipe() => "CodexMeterShutdownTest-" + Guid.NewGuid().ToString("N");

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
