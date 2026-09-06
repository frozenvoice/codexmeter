using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Companion;

public sealed class CompanionPipeServer : IDisposable
{
    private readonly CompanionRequestHub _hub;
    private readonly CompanionPairingState _pairing;
    private readonly AppLog _log;
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private string? _hostExitReason;

    public CompanionPipeServer(CompanionRequestHub hub, CompanionPairingState pairing, AppLog log, string? pipeName = null)
    {
        _hub = hub;
        _pairing = pairing;
        _log = log;
        _pipeName = pipeName ?? NativeMessagingHost.PipeName;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ListenAsync(_cts.Token));
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            var generation = 0;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                generation = _hub.BeginConnection();
                _hostExitReason = null;
                _log.Info(CompanionHostLifecycle.PipeAcceptedLine(generation));
                await ServeAsync(pipe, generation, cancellationToken).ConfigureAwait(false);
                _log.Info(CompanionHostLifecycle.PipeEofLine(generation));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("companion pipe: " + AppLog.Sanitize(ex.GetType().Name));
                _hostExitReason ??= CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipePumpFailed);
            }
            finally
            {
                if (_hub.IsConnected)
                {
                    _log.Info("companion disconnected");
                }

                var closeReason = string.IsNullOrWhiteSpace(_hostExitReason)
                    ? CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipeServerEof)
                    : _hostExitReason;
                if (generation != 0)
                {
                    _log.Info(CompanionHostLifecycle.PipeClosedLine(generation, closeReason));
                }

                _hub.Disconnect(generation);
                pipe?.Dispose();
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, int generation, CancellationToken cancellationToken)
    {
        var writes = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var writeLock = new SemaphoreSlim(1, 1);

        void Enqueue(CompanionBridgeMessage message)
        {
            var json = CompanionBridgeProtocol.Serialize(message);
            if (Encoding.UTF8.GetByteCount(json) > CompanionBridgeProtocol.MaxCommandBytes
                && string.Equals(message.Type, CompanionBridgeProtocol.Invoke, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(message.RequestId))
                {
                    _hub.TryComplete(message.RequestId, new ProviderResponse
                    {
                        Status = 0,
                        Error = "PayloadTooLarge",
                        SchemaMismatch = false
                    }, generation);
                }

                return;
            }

            if (!CompanionOutgoingQueue.TryWrite(writes.Writer, json))
            {
                if (string.Equals(message.Type, CompanionBridgeProtocol.Invoke, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(message.RequestId))
                {
                    _hub.TryComplete(message.RequestId, CompanionBridgeProtocol.WriteFailureResponse(), generation);
                }
            }
        }

        void OnOutgoing(CompanionBridgeMessage message) => Enqueue(message);
        _hub.Outgoing += OnOutgoing;

        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var payload in writes.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await NativeMessagingFraming.WriteMessageAsync(pipe, payload, writeLock, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);

        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                var raw = await NativeMessagingFraming.ReadMessageAsync(pipe, CompanionBridgeProtocol.MaxNativeMessageBytes, cancellationToken).ConfigureAwait(false);
                if (raw is null)
                {
                    break;
                }

                await HandleIncomingAsync(raw, generation, Enqueue).ConfigureAwait(false);
            }
        }
        finally
        {
            _hub.Outgoing -= OnOutgoing;
            writes.Writer.TryComplete();
            try
            {
                await writer.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private Task HandleIncomingAsync(string raw, int generation, Action<CompanionBridgeMessage> enqueue)
    {
        var parsed = CompanionBridgeProtocol.Parse(raw);
        if (!parsed.Accepted || parsed.Message is null)
        {
            enqueue(new CompanionBridgeMessage
            {
                Type = CompanionBridgeProtocol.ErrorType,
                SchemaMismatch = parsed.Error != "PayloadTooLarge",
                PayloadTooLarge = parsed.Error == "PayloadTooLarge",
                Error = parsed.Error
            });
            if (parsed.Error == "PayloadTooLarge")
            {
                _hub.FailAllPending(CompanionChunkProtocol.PayloadTooLargeResponse());
            }

            return Task.CompletedTask;
        }

        var message = parsed.Message;
        if (CompanionHostLifecycle.IsMessage(message))
        {
            if (CompanionHostLifecycle.TryGetReason(message, out var reason))
            {
                _hostExitReason = reason;
                _log.Info(CompanionHostLifecycle.LifecycleReceivedLine(generation, reason));
            }

            return Task.CompletedTask;
        }

        if (!CompanionPairingStore.TokensEqual(message.PairingToken, _pairing.Token)
            && !string.Equals(message.Type, CompanionBridgeProtocol.Hello, StringComparison.OrdinalIgnoreCase))
        {
            enqueue(new CompanionBridgeMessage { Type = CompanionBridgeProtocol.ErrorType, Error = "pairing required" });
            return Task.CompletedTask;
        }

        if (string.Equals(message.Type, CompanionBridgeProtocol.Hello, StringComparison.OrdinalIgnoreCase))
        {
            var ok = CompanionPairingStore.TokensEqual(message.PairingToken, _pairing.Token);
            _hub.TryAcceptHello(generation, ok);
            if (ok)
            {
                _log.Info("companion reconnected");
            }

            enqueue(new CompanionBridgeMessage
            {
                Type = CompanionBridgeProtocol.HelloAck,
                Accepted = ok,
                Error = ok ? null : "pairing required"
            });
            return Task.CompletedTask;
        }

        if (CompanionChunkProtocol.IsChunkFrame(message.Type)
            && !string.IsNullOrWhiteSpace(message.RequestId))
        {
            _hub.TryHandleChunkedResult(parsed, generation);
            return Task.CompletedTask;
        }

        if (string.Equals(message.Type, CompanionBridgeProtocol.InvokeResult, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(message.RequestId))
        {
            _hub.TryCompleteInvokeResult(message.RequestId, parsed, generation);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _hub.FailAllPending(CompanionBridgeProtocol.DisconnectResponse());
    }
}

public static class CompanionOutgoingQueue
{
    public static bool TryWrite(ChannelWriter<string> writer, string json) =>
        writer.TryWrite(json);
}
