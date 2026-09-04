namespace ProMeter.Providers.ChatGpt;

public delegate Task<ProviderResponse> RawAuthenticatedFetch(
    string method,
    string path,
    string? jsonBody,
    string? accessToken,
    CancellationToken cancellationToken);

public sealed class SessionAuthCoordinator
{
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly object _flightLock = new();
    private Task<ProviderResponse>? _sessionFlight;
    private string? _accessToken;
    private DateTimeOffset? _expiresAt;
    private bool _sessionKnown;

    public int SessionFetchCount { get; private set; }

    public bool HasCachedSession => _sessionKnown;

    public void Invalidate()
    {
        _accessToken = null;
        _expiresAt = null;
        _sessionKnown = false;
    }

    public bool NeedsSession(DateTimeOffset now) =>
        !_sessionKnown || (_expiresAt is DateTimeOffset expires && expires <= now.AddMinutes(1));

    public static bool IsSessionPath(string path)
    {
        if (!BackendTargetPolicy.TryValidate(path, out var normalized, out _))
        {
            var relative = path ?? "";
            var query = relative.IndexOf('?', StringComparison.Ordinal);
            if (query >= 0)
            {
                relative = relative[..query];
            }

            return string.Equals(relative, ChatGptEndpoints.Session, StringComparison.OrdinalIgnoreCase);
        }

        var cut = normalized.IndexOf('?', StringComparison.Ordinal);
        var withoutQuery = cut >= 0 ? normalized[..cut] : normalized;
        return string.Equals(withoutQuery, ChatGptEndpoints.Session, StringComparison.OrdinalIgnoreCase);
    }

    public void ApplySession(JsonNode? session, DateTimeOffset now)
    {
        if (session is null)
        {
            Invalidate();
            return;
        }

        _sessionKnown = true;
        _accessToken = ChatGptJson.GetString(session, "accessToken", "access_token");
        var expires = TimestampParser.ToDateTimeOffset(session, "expires", "expires_at", "expiresAt");
        _expiresAt = expires > now ? expires : null;
    }

    public async Task<ProviderResponse> SendAsync(
        RawAuthenticatedFetch fetch,
        string method,
        string path,
        string? jsonBody,
        CancellationToken cancellationToken,
        DateTimeOffset? now = null)
    {
        var clock = now ?? DateTimeOffset.UtcNow;
        if (IsSessionPath(path))
        {
            return await SharedSessionFetchAsync(fetch, clock, cancellationToken);
        }

        if (NeedsSession(clock))
        {
            var session = await SharedSessionFetchAsync(fetch, clock, cancellationToken);
            if (IsUnusableSession(session))
            {
                return session;
            }
        }

        var response = await fetch(method, path, jsonBody, _accessToken, cancellationToken);
        if (!response.IsUnauthorized)
        {
            return response;
        }

        _accessToken = null;
        var refresh = await SharedSessionFetchAsync(fetch, DateTimeOffset.UtcNow, cancellationToken);
        if (IsUnusableSession(refresh))
        {
            return refresh;
        }

        return await fetch(method, path, jsonBody, _accessToken, cancellationToken);
    }

    private async Task<ProviderResponse> SharedSessionFetchAsync(
        RawAuthenticatedFetch fetch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Task<ProviderResponse> flight;
        lock (_flightLock)
        {
            if (_sessionFlight is { IsCompleted: false } existing)
            {
                flight = existing;
            }
            else
            {
                flight = FetchSessionCoreAsync(fetch, now);
                _sessionFlight = flight;
            }
        }

        return await flight.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderResponse> FetchSessionCoreAsync(RawAuthenticatedFetch fetch, DateTimeOffset now)
    {
        await _sessionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            SessionFetchCount++;
            var response = await fetch("GET", ChatGptEndpoints.Session, null, null, CancellationToken.None).ConfigureAwait(false);
            if (response.IsSuccess)
            {
                ApplySession(ChatGptJson.ParseNode(response.Body), now);
            }
            else
            {
                Invalidate();
            }

            return response;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static bool IsUnusableSession(ProviderResponse response) =>
        !response.IsSuccess
        || response.SchemaMismatch
        || response.IsUnauthorized
        || response.IsRateLimited
        || response.IsOffline
        || response.IsServerError;
}
