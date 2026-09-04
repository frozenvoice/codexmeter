using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Companion;

public sealed class CompanionPipeServer : IDisposable
{
    private readonly CompanionRequestHub _hub;
    private readonly CompanionPairingState _pairing;
    private readonly AppLog _log;
    private CancellationTokenSource? _cts;

    public CompanionPipeServer(CompanionRequestHub hub, CompanionPairingState pairing, AppLog log)
    {
        _hub = hub;
        _pairing = pairing;
        _log = log;
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
            try
            {
                using var pipe = new NamedPipeServerStream(NativeMessagingHost.PipeName, PipeDirection.InOut, 1);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                Serve(pipe, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.Warn("companion pipe: " + AppLog.Sanitize(ex.Message));
            }
            finally
            {
                _hub.IsConnected = false;
            }
        }
    }

    private void Serve(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        void OnOutgoing(CompanionBridgeMessage message)
        {
            NativeMessagingHost.WriteMessage(pipe, CompanionBridgeProtocol.Serialize(message));
        }

        _hub.Outgoing += OnOutgoing;
        try
        {
            while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
            {
                if (!NativeMessagingHost.TryReadMessage(pipe, out var raw))
                {
                    break;
                }

                var parsed = CompanionBridgeProtocol.Parse(raw);
                if (!parsed.Accepted || parsed.Message is null)
                {
                    NativeMessagingHost.WriteMessage(pipe, JsonSerializer.Serialize(new
                    {
                        type = "error",
                        schemaMismatch = true,
                        error = parsed.Error
                    }));
                    continue;
                }

                var message = parsed.Message;
                if (!CompanionPairingStore.TokensEqual(message.PairingToken, _pairing.Token)
                    && !string.Equals(message.Type, CompanionBridgeProtocol.Hello, StringComparison.OrdinalIgnoreCase))
                {
                    NativeMessagingHost.WriteMessage(pipe, JsonSerializer.Serialize(new
                    {
                        type = "error",
                        error = "pairing required"
                    }));
                    continue;
                }

                if (string.Equals(message.Type, CompanionBridgeProtocol.Hello, StringComparison.OrdinalIgnoreCase))
                {
                    var ok = CompanionPairingStore.TokensEqual(message.PairingToken, _pairing.Token);
                    _hub.IsConnected = ok;
                    NativeMessagingHost.WriteMessage(pipe, JsonSerializer.Serialize(new
                    {
                        type = "helloAck",
                        accepted = ok
                    }, CompanionBridgeProtocol.JsonOptions));
                    continue;
                }

                if (string.Equals(message.Type, CompanionBridgeProtocol.FetchResult, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(message.RequestId))
                {
                    var sanitized = BrowserResponseSanitizer.Sanitize(ChatGptJson.ParseNode(message.Body));
                    message.Body = sanitized?.ToJsonString();
                    _hub.TryComplete(message.RequestId, CompanionBridgeProtocol.ToProviderResponse(new CompanionParseResult
                    {
                        Accepted = true,
                        Message = message
                    }));
                }
            }
        }
        finally
        {
            _hub.Outgoing -= OnOutgoing;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
