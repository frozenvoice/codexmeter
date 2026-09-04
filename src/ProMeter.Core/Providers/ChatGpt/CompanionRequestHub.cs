namespace ProMeter.Providers.ChatGpt;

public interface ICompanionRequestHub
{
    bool IsConnected { get; }
    Task<ProviderResponse> RequestAsync(string method, string path, string? jsonBody, CancellationToken cancellationToken);
    bool TryComplete(string requestId, ProviderResponse response);
}

public sealed class CompanionRequestHub : ICompanionRequestHub
{
    private readonly Dictionary<string, TaskCompletionSource<ProviderResponse>> _pending = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    public event Action<CompanionBridgeMessage>? Outgoing;

    public bool IsConnected { get; set; }

    public async Task<ProviderResponse> RequestAsync(string method, string path, string? jsonBody, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            return new ProviderResponse { Status = 0, Error = "browser companion is not connected" };
        }

        if (!BackendTargetPolicy.TryValidate(path, out var safe, out var error))
        {
            return new ProviderResponse { Status = 0, Error = error, SchemaMismatch = true };
        }

        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<ProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending[id] = tcs;
        }

        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        Outgoing?.Invoke(new CompanionBridgeMessage
        {
            Type = CompanionBridgeProtocol.Fetch,
            RequestId = id,
            Method = method,
            Path = safe,
            Body = jsonBody
        });

        try
        {
            return await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }

            throw;
        }
    }

    public bool TryComplete(string requestId, ProviderResponse response)
    {
        TaskCompletionSource<ProviderResponse>? tcs;
        lock (_gate)
        {
            if (!_pending.Remove(requestId, out tcs))
            {
                return false;
            }
        }

        return tcs.TrySetResult(response);
    }
}

public sealed class BrowserCompanionTransport : IChatGptTransport
{
    private readonly ICompanionRequestHub _hub;
    private readonly SessionAuthCoordinator _auth = new();

    public BrowserCompanionTransport(ICompanionRequestHub hub)
    {
        _hub = hub;
    }

    public Task<ProviderResponse> SendAsync(string method, string path, string? jsonBody = null, CancellationToken cancellationToken = default)
    {
        if (!BackendTargetPolicy.TryValidate(path, out var safe, out var error))
        {
            return Task.FromResult(new ProviderResponse { Status = 0, Error = error, SchemaMismatch = true });
        }

        return _auth.SendAsync(RawFetch, method, safe, jsonBody, cancellationToken);
    }

    private Task<ProviderResponse> RawFetch(string method, string path, string? jsonBody, string? accessToken, CancellationToken cancellationToken)
    {
        _ = accessToken;
        return _hub.RequestAsync(method, path, jsonBody, cancellationToken);
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
