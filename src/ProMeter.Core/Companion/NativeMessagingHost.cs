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

    public static bool ShouldRun(string[] args, CompanionPairingState pairing) =>
        CompanionCallerOrigin.IsAllowed(args, pairing);

    public static void Run(string[]? args = null)
    {
        try
        {
            if (!ShouldRun(args ?? Environment.GetCommandLineArgs(), CompanionPairingStore.LoadOrCreate()))
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
    public static bool IsAllowed(string[]? args, CompanionPairingState? pairing = null)
    {
        if (!TryResolveExtensionId(args, out var extensionId))
        {
            return false;
        }

        pairing ??= CompanionPairingStore.LoadOrCreate();
        return IsRegisteredId(extensionId, pairing);
    }

    public static bool TryResolveExtensionId(string[]? args, out string extensionId)
    {
        extensionId = "";
        if (args is null || args.Length == 0)
        {
            return false;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var arg in args)
        {
            if (IsUnrelatedArgument(arg))
            {
                continue;
            }

            if (!TryParseChromeExtensionOrigin(arg, out var id))
            {
                continue;
            }

            ids.Add(id);
            if (ids.Count > 1)
            {
                return false;
            }
        }

        if (ids.Count != 1)
        {
            return false;
        }

        extensionId = ids.First();
        return true;
    }

    public static bool TryParseChromeExtensionOrigin(string? value, out string extensionId)
    {
        extensionId = "";
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, "chrome-extension", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
            || !uri.IsDefaultPort
            || !CompanionHostManifest.IsExtensionId(uri.Host))
        {
            return false;
        }

        extensionId = uri.Host;
        return true;
    }

    private static bool IsRegisteredId(string extensionId, CompanionPairingState pairing) =>
        string.Equals(extensionId, pairing.ChromeExtensionId, StringComparison.OrdinalIgnoreCase)
        || string.Equals(extensionId, pairing.EdgeExtensionId, StringComparison.OrdinalIgnoreCase);

    private static bool IsUnrelatedArgument(string? arg)
    {
        if (string.IsNullOrWhiteSpace(arg))
        {
            return true;
        }

        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            return true;
        }

        return arg.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
               || (arg.Contains('\\', StringComparison.Ordinal)
                   || (arg.Contains('/', StringComparison.Ordinal)
                       && !arg.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)));
    }
}
