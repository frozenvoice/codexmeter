namespace ProMeter.Providers.ChatGpt;

public interface ICompanionRequestHub
{
    bool IsConnected { get; }
    int ConnectionGeneration { get; }
    event Action<CompanionBridgeMessage>? Outgoing;
    event Action? ConnectionChanged;
    int BeginConnection();
    bool TryAcceptHello(int generation, bool pairingAccepted);
    void Disconnect(int generation);
    Task<ProviderResponse> RequestAsync(CompanionOperation operation, CompanionOperationArgs? args, CancellationToken cancellationToken);
    bool TryComplete(string requestId, ProviderResponse response, int generation);
    bool TryGetPendingOperation(string requestId, int generation, out CompanionOperation operation);
    bool TryCompleteInvokeResult(string requestId, CompanionParseResult parsed, int generation);
    void FailAllPending(ProviderResponse reason);
}

public sealed class CompanionRequestHub : ICompanionRequestHub
{
    private readonly Dictionary<string, (TaskCompletionSource<ProviderResponse> Tcs, int Generation, CompanionOperation Operation)> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _generation;

    public TimeSpan RequestTimeout { get; init; } = CompanionBridgeProtocol.DefaultRequestTimeout;
    public bool IsConnected { get; private set; }
    public int ConnectionGeneration { get; private set; }

    public event Action<CompanionBridgeMessage>? Outgoing;
    public event Action? ConnectionChanged;

    public int BeginConnection()
    {
        lock (_gate)
        {
            FailAllPendingLocked(CompanionBridgeProtocol.DisconnectResponse());
            ConnectionGeneration = ++_generation;
            IsConnected = false;
        }

        ConnectionChanged?.Invoke();
        return ConnectionGeneration;
    }

    public bool TryAcceptHello(int generation, bool pairingAccepted)
    {
        lock (_gate)
        {
            if (generation != ConnectionGeneration)
            {
                return false;
            }

            IsConnected = pairingAccepted;
        }

        ConnectionChanged?.Invoke();
        return pairingAccepted;
    }

    public void Disconnect(int generation)
    {
        lock (_gate)
        {
            if (generation != ConnectionGeneration)
            {
                return;
            }

            IsConnected = false;
            FailAllPendingLocked(CompanionBridgeProtocol.DisconnectResponse());
        }

        ConnectionChanged?.Invoke();
    }

    public async Task<ProviderResponse> RequestAsync(CompanionOperation operation, CompanionOperationArgs? args, CancellationToken cancellationToken)
    {
        if (!CompanionOperationRouter.TryBuild(operation, args, out _, out _, out _, out var error))
        {
            return new ProviderResponse { Status = 0, Error = error, SchemaMismatch = true };
        }

        string id;
        TaskCompletionSource<ProviderResponse> tcs;
        int generation;
        lock (_gate)
        {
            if (!IsConnected)
            {
                return CompanionBridgeProtocol.NotConnectedResponse();
            }

            id = Guid.NewGuid().ToString("N");
            tcs = new TaskCompletionSource<ProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            generation = ConnectionGeneration;
            _pending[id] = (tcs, generation, operation);
        }

        var message = new CompanionBridgeMessage
        {
            Type = CompanionBridgeProtocol.Invoke,
            RequestId = id,
            Operation = operation.ToString(),
            Args = args
        };

        try
        {
            Outgoing?.Invoke(message);
        }
        catch
        {
            TryComplete(id, CompanionBridgeProtocol.WriteFailureResponse(), generation);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!TryComplete(id, CompanionBridgeProtocol.TimeoutResponse(), generation))
            {
                return await tcs.Task.ConfigureAwait(false);
            }

            return CompanionBridgeProtocol.TimeoutResponse();
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }

            tcs.TrySetCanceled(cancellationToken);
            throw;
        }
    }

    public bool TryComplete(string requestId, ProviderResponse response, int generation)
    {
        TaskCompletionSource<ProviderResponse>? tcs;
        lock (_gate)
        {
            if (!_pending.TryGetValue(requestId, out var pending) || pending.Generation != generation)
            {
                return false;
            }

            _pending.Remove(requestId);
            tcs = pending.Tcs;
        }

        return tcs.TrySetResult(response);
    }

    public bool TryGetPendingOperation(string requestId, int generation, out CompanionOperation operation)
    {
        lock (_gate)
        {
            if (_pending.TryGetValue(requestId, out var pending) && pending.Generation == generation)
            {
                operation = pending.Operation;
                return true;
            }
        }

        operation = default;
        return false;
    }

    public bool TryCompleteInvokeResult(string requestId, CompanionParseResult parsed, int generation)
    {
        if (!TryGetPendingOperation(requestId, generation, out var pendingOperation))
        {
            return false;
        }

        var declared = parsed.Message?.Operation;
        if (!Enum.TryParse<CompanionOperation>(declared, ignoreCase: true, out var resultOperation)
            || !Enum.IsDefined(resultOperation)
            || resultOperation != pendingOperation)
        {
            return TryComplete(requestId, CompanionBridgeProtocol.OperationMismatchResponse(), generation);
        }

        var response = CompanionBridgeProtocol.ToProviderResponse(parsed, pendingOperation);
        return TryComplete(requestId, response, generation);
    }

    public void FailAllPending(ProviderResponse reason)
    {
        lock (_gate)
        {
            FailAllPendingLocked(reason);
        }
    }

    private void FailAllPendingLocked(ProviderResponse reason)
    {
        foreach (var pending in _pending.Values)
        {
            pending.Tcs.TrySetResult(reason);
        }

        _pending.Clear();
    }
}

public static class CompanionReconnectGrace
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(8);

    public static async Task<bool> WaitUntilConnectedAsync(
        ICompanionRequestHub hub,
        TimeSpan grace,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        if (hub.IsConnected)
        {
            return true;
        }

        log?.Invoke("sync waiting for companion reconnect");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (hub.IsConnected)
            {
                tcs.TrySetResult(true);
            }
        }

        hub.ConnectionChanged += OnChanged;
        try
        {
            if (hub.IsConnected)
            {
                return true;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(grace);
            using var registration = linked.Token.Register(() => tcs.TrySetResult(false));
            var connected = await tcs.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return connected;
        }
        finally
        {
            hub.ConnectionChanged -= OnChanged;
        }
    }
}

public sealed class BrowserCompanionTransport : IChatGptTransport
{
    private readonly ICompanionRequestHub _hub;
    private readonly Action<string>? _log;
    private readonly TimeSpan _reconnectGrace;

    public BrowserCompanionTransport(
        ICompanionRequestHub hub,
        Action<string>? log = null,
        TimeSpan? reconnectGrace = null)
    {
        _hub = hub;
        _log = log;
        _reconnectGrace = reconnectGrace is { } grace && grace > TimeSpan.Zero
            ? grace
            : CompanionReconnectGrace.DefaultDuration;
    }

    public async Task<ProviderResponse> SendAsync(string method, string path, string? jsonBody = null, CancellationToken cancellationToken = default)
    {
        if (!CompanionOperationRouter.TryMap(method, path, jsonBody, out var operation, out var args, out var error))
        {
            return new ProviderResponse { Status = 0, Error = error, SchemaMismatch = true };
        }

        if (!_hub.IsConnected)
        {
            var recovered = await CompanionReconnectGrace.WaitUntilConnectedAsync(
                _hub,
                _reconnectGrace,
                _log,
                cancellationToken).ConfigureAwait(false);
            if (!recovered)
            {
                return CompanionBridgeProtocol.NotConnectedResponse();
            }
        }

        var response = await _hub.RequestAsync(operation, args, cancellationToken).ConfigureAwait(false);
        if (!response.IsCompanionDisconnected)
        {
            return response;
        }

        _log?.Invoke($"companion request interrupted operation={operation} waiting-reconnect");
        if (!CompanionOperationPolicy.IsReconnectRetrySafe(operation))
        {
            return response;
        }

        var retried = await CompanionReconnectGrace.WaitUntilConnectedAsync(
            _hub,
            _reconnectGrace,
            _log,
            cancellationToken).ConfigureAwait(false);
        if (!retried)
        {
            return response;
        }

        _log?.Invoke($"companion reconnected retrying operation={operation}");
        return await _hub.RequestAsync(operation, args, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class DataExportTransport : IChatGptTransport
{
    public Task<ProviderResponse> SendAsync(string method, string path, string? jsonBody = null, CancellationToken cancellationToken = default)
    {
        _ = method;
        _ = path;
        _ = jsonBody;
        _ = cancellationToken;
        return Task.FromResult(new ProviderResponse
        {
            Status = 0,
            Error = "Data Export mode does not call ChatGPT endpoints. Import conversations.json instead."
        });
    }
}
