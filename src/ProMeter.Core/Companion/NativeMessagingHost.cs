using System.IO.Pipes;
using System.Text;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Companion;

public static class NativeMessagingFraming
{
    public static async Task<string?> ReadMessageAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, 4, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0)
        {
            return null;
        }

        if (length > maxBytes)
        {
            await DiscardAsync(stream, length, cancellationToken).ConfigureAwait(false);
            return CompanionBridgeProtocol.Serialize(new CompanionBridgeMessage
            {
                Type = CompanionBridgeProtocol.ErrorType,
                Error = "PayloadTooLarge",
                PayloadTooLarge = true,
                SchemaMismatch = true
            });
        }

        var payload = new byte[length];
        if (!await ReadExactAsync(stream, payload, length, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return Encoding.UTF8.GetString(payload);
    }

    public static async Task WriteMessageAsync(Stream stream, string message, SemaphoreSlim writeLock, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        if (payload.Length > CompanionBridgeProtocol.MaxNativeMessageBytes)
        {
            payload = Encoding.UTF8.GetBytes(CompanionBridgeProtocol.Serialize(new CompanionBridgeMessage
            {
                Type = CompanionBridgeProtocol.ErrorType,
                Error = "PayloadTooLarge",
                PayloadTooLarge = true,
                SchemaMismatch = true
            }));
        }

        var header = BitConverter.GetBytes(payload.Length);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private static async Task DiscardAsync(Stream stream, int remaining, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(remaining, 8192)];
        while (remaining > 0)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (n <= 0)
            {
                return;
            }

            remaining -= n;
        }
    }
}

public static class NativeMessagingHost
{
    public const string PipeName = "ProMeterCompanion";

    public static void Run(string[]? args = null)
    {
        try
        {
            if (!CompanionCallerOrigin.IsAllowed(args ?? Environment.GetCommandLineArgs()))
            {
                return;
            }

            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cts.Cancel();
            };
            RunPumpsAsync(stdin, stdout, pipe, cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }

    public static async Task RunPumpsAsync(Stream chromeIn, Stream chromeOut, Stream pipe, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var chromeWrite = new SemaphoreSlim(1, 1);
        var pipeWrite = new SemaphoreSlim(1, 1);
        var chromeToPipe = PumpAsync(chromeIn, pipe, pipeWrite, attachPairing: true, linked.Token);
        var pipeToChrome = PumpAsync(pipe, chromeOut, chromeWrite, attachPairing: false, linked.Token);
        var completed = await Task.WhenAny(chromeToPipe, pipeToChrome).ConfigureAwait(false);
        linked.Cancel();
        try
        {
            await Task.WhenAll(chromeToPipe, pipeToChrome).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (completed.IsFaulted)
        {
            completed.GetAwaiter().GetResult();
        }
    }

    private static async Task PumpAsync(
        Stream source,
        Stream destination,
        SemaphoreSlim destinationLock,
        bool attachPairing,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var max = attachPairing
                ? CompanionBridgeProtocol.MaxNativeMessageBytes
                : CompanionBridgeProtocol.MaxCommandBytes;
            var message = await NativeMessagingFraming.ReadMessageAsync(source, max, cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                return;
            }

            var forwarded = attachPairing
                ? AttachPairingToken(message)
                : StripPairingToken(message);
            await NativeMessagingFraming.WriteMessageAsync(destination, forwarded, destinationLock, cancellationToken).ConfigureAwait(false);
        }
    }

    public static string AttachPairingToken(string message)
    {
        try
        {
            if (JsonNode.Parse(message) is not JsonObject node)
            {
                return message;
            }

            var existing = ChatGptJson.GetString(node, "pairingToken", "pairing_token");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return message;
            }

            node["pairingToken"] = CompanionPairingStore.LoadOrCreate().Token;
            return node.ToJsonString();
        }
        catch
        {
            return message;
        }
    }

    public static string StripPairingToken(string message)
    {
        try
        {
            if (JsonNode.Parse(message) is not JsonObject node)
            {
                return message;
            }

            node.Remove("pairingToken");
            node.Remove("pairing_token");
            return node.ToJsonString();
        }
        catch
        {
            return message;
        }
    }
}

public static class CompanionCallerOrigin
{
    public static bool IsAllowed(string[] args)
    {
        var origin = args.Skip(1).FirstOrDefault(arg => arg.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (!Uri.TryCreate(origin.TrimEnd('/'), UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "chrome-extension", StringComparison.OrdinalIgnoreCase)
            || !CompanionHostManifest.IsExtensionId(uri.Host))
        {
            return false;
        }

        var pairing = CompanionPairingStore.LoadOrCreate();
        return string.Equals(uri.Host, pairing.ChromeExtensionId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Host, pairing.EdgeExtensionId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(uri.Host, pairing.ExtensionId, StringComparison.OrdinalIgnoreCase);
    }
}
