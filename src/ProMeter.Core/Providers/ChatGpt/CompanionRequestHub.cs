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
                return new ProviderResponse { Status = 0, Error = "browser companion is not connected" };
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
            TryComplete(id, new ProviderResponse { Status = 0, Error = "bridge write failed" }, generation);
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

public sealed class BrowserCompanionTransport : IChatGptTransport
{
    private readonly ICompanionRequestHub _hub;

    public BrowserCompanionTransport(ICompanionRequestHub hub)
    {
        _hub = hub;
    }

    public Task<ProviderResponse> SendAsync(string method, string path, string? jsonBody = null, CancellationToken cancellationToken = default)
    {
        if (!CompanionOperationRouter.TryMap(method, path, jsonBody, out var operation, out var args, out var error))
        {
            return Task.FromResult(new ProviderResponse { Status = 0, Error = error, SchemaMismatch = true });
        }

        return _hub.RequestAsync(operation, args, cancellationToken);
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
