namespace ProMeter.Providers.ChatGpt;

public delegate Task<ProviderResponse> RawAuthenticatedFetch(
    string method,
    string path,
    string? jsonBody,
    string? accessToken,
    CancellationToken cancellationToken);

public sealed class SessionAuthCoordinator
{
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var relative = path;
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            relative = uri.AbsolutePath;
        }

        var query = relative.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            relative = relative[..query];
        }

        return string.Equals(relative, ChatGptEndpoints.Session, StringComparison.OrdinalIgnoreCase);
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
            return await FetchSessionAsync(fetch, cancellationToken, clock);
        }

        if (NeedsSession(clock))
        {
            var session = await FetchSessionAsync(fetch, cancellationToken, clock);
            if (IsFatalTransport(session))
            {
                return session;
            }
        }

        var response = await fetch(method, path, jsonBody, _accessToken, cancellationToken);
        if (!response.IsUnauthorized)
        {
            return response;
        }

        Invalidate();
        var refresh = await FetchSessionAsync(fetch, cancellationToken, DateTimeOffset.UtcNow);
        if (IsFatalTransport(refresh) && !refresh.IsSuccess)
        {
            return response;
        }

        return await fetch(method, path, jsonBody, _accessToken, cancellationToken);
    }

    private async Task<ProviderResponse> FetchSessionAsync(
        RawAuthenticatedFetch fetch,
        CancellationToken cancellationToken,
        DateTimeOffset now)
    {
        SessionFetchCount++;
        var response = await fetch("GET", ChatGptEndpoints.Session, null, null, cancellationToken);
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

    private static bool IsFatalTransport(ProviderResponse response) =>
        response.SchemaMismatch
        || response.IsUnauthorized
        || response.IsRateLimited
        || response.IsOffline;
}
