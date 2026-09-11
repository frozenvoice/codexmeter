using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Companion;

public sealed class CompanionPipeServer : IDisposable
{
    public static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(1000);

    private readonly CompanionRequestHub _hub;
    private readonly CompanionPairingState _pairing;
    private readonly AppLog _log;
    private readonly string _pipeName;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Task? _stopTask;
    private NamedPipeServerStream? _activePipe;
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
        lock (_gate)
        {
            if (_listenTask is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _listenTask = Task.Run(() => ListenAsync(_cts.Token));
        }
    }

    public Task StopAsync() => StopAsync(StopTimeout);

    public Task StopAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync(timeout);
            return _stopTask;
        }
    }

    private async Task StopCoreAsync(TimeSpan timeout)
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
        }

        NamedPipeServerStream? pipe;
        lock (_gate)
        {
            pipe = _activePipe;
        }

        try
        {
            pipe?.Dispose();
        }
        catch
        {
        }

        _hub.FailAllPending(CompanionBridgeProtocol.DisconnectResponse());

        Task? listen;
        lock (_gate)
        {
            listen = _listenTask;
        }

        if (listen is not null)
        {
            try
            {
                await listen.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
            }
        }

        NamedPipeServerStream? leftover;
        lock (_gate)
        {
            leftover = _activePipe;
        }

        try
        {
            leftover?.Dispose();
        }
        catch
        {
        }

        if (listen is null || listen.IsCompleted)
        {
            try
            {
                _cts?.Dispose();
            }
            catch
            {
            }

            lock (_gate)
            {
                _cts = null;
            }
        }
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
                SetActivePipe(pipe);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

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
                if (!cancellationToken.IsCancellationRequested)
                {
                    _log.Warn("companion pipe: " + AppLog.Sanitize(ex.GetType().Name));
                    _hostExitReason ??= CompanionHostLifecycle.ToWire(CompanionHostLifecycleReason.PipePumpFailed);
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                ClearActivePipe(pipe);
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
                try
                {
                    pipe?.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private void SetActivePipe(NamedPipeServerStream pipe)
    {
        lock (_gate)
        {
            _activePipe = pipe;
        }
    }

    private void ClearActivePipe(NamedPipeServerStream? pipe)
    {
        if (pipe is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_activePipe, pipe))
            {
                _activePipe = null;
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
            catch
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
                await writer.WaitAsync(TimeSpan.FromMilliseconds(250)).ConfigureAwait(false);
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
        try
        {
            StopAsync(StopTimeout).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}

public static class CompanionOutgoingQueue
{
    public static bool TryWrite(ChannelWriter<string> writer, string json) =>
        writer.TryWrite(json);
}
