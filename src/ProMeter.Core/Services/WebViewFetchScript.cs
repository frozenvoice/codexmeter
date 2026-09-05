namespace ProMeter.Services;

public static class WebViewFetchScript
{
    public const int RequestTimeoutMilliseconds = 20_000;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(RequestTimeoutMilliseconds);
    public static readonly TimeSpan ScriptGuardTimeout = TimeSpan.FromSeconds(25);

    public static string Build(string method, string path, string? body, string? accessToken)
    {
        var methodJson = JsonSerializer.Serialize(method);
        var pathJson = JsonSerializer.Serialize(path);
        var bodyJson = body is null ? "null" : JsonSerializer.Serialize(body);
        var tokenJson = accessToken is null ? "null" : JsonSerializer.Serialize(accessToken);
        return $$"""
            (async () => {
              const controller = new AbortController();
              const timer = setTimeout(() => controller.abort(), {{RequestTimeoutMilliseconds}});
              try {
                const token = {{tokenJson}};
                const headers = { 'Accept': 'application/json' };
                if (token) headers['Authorization'] = 'Bearer ' + token;
                const init = { method: {{methodJson}}, credentials: 'include', headers, signal: controller.signal };
                const body = {{bodyJson}};
                if (body) {
                  headers['Content-Type'] = 'application/json';
                  init.body = body;
                }
                const res = await fetch({{pathJson}}, init);
                const text = await res.text();
                return JSON.stringify({
                  status: res.status,
                  retryAfter: res.headers.get('retry-after'),
                  body: text
                });
              } catch (error) {
                const timeout = error && (error.name === 'AbortError' || error.name === 'TimeoutError');
                return JSON.stringify({
                  status: 0,
                  error: timeout ? '{{WebViewInitializationCodes.RequestTimeout}}' : '{{WebViewInitializationCodes.RequestFailed}}'
                });
              } finally {
                clearTimeout(timer);
              }
            })()
            """;
    }

    public static string MapJavaScriptError(string? name, string? message = null)
    {
        _ = message;
        return name is "AbortError" or "TimeoutError"
            ? WebViewInitializationCodes.RequestTimeout
            : WebViewInitializationCodes.RequestFailed;
    }

    public static string? CanonicalizeError(string? error) => error switch
    {
        WebViewInitializationCodes.RequestTimeout => WebViewInitializationCodes.RequestTimeout,
        WebViewInitializationCodes.ScriptExecutionTimeout => WebViewInitializationCodes.ScriptExecutionTimeout,
        WebViewInitializationCodes.RequestFailed => WebViewInitializationCodes.RequestFailed,
        WebViewInitializationCodes.Cancelled => WebViewInitializationCodes.Cancelled,
        WebViewInitializationCodes.Timeout => WebViewInitializationCodes.Timeout,
        WebViewInitializationCodes.NavigationTimeout => WebViewInitializationCodes.NavigationTimeout,
        WebViewInitializationCodes.NavigationFailed => WebViewInitializationCodes.NavigationFailed,
        null or "" => null,
        _ => WebViewInitializationCodes.RequestFailed
    };

    public static ProviderResponse MapScriptResult(string? decoded)
    {
        var node = ChatGptJson.ParseNode(decoded);
        if (node is null)
        {
            return new ProviderResponse
            {
                Status = 0,
                Error = WebViewInitializationCodes.RequestFailed,
                SchemaMismatch = true
            };
        }

        var error = CanonicalizeError(ChatGptJson.GetString(node, "error"));
        return new ProviderResponse
        {
            Status = (int)(ChatGptJson.GetDouble(node, "status") ?? 0),
            RetryAfter = ChatGptJson.GetString(node, "retryAfter"),
            Body = error is null ? ChatGptJson.GetString(node, "body") ?? "" : "",
            Error = error,
            SchemaMismatch = ChatGptJson.GetBool(node, "schemaMismatch") == true
        };
    }

    public static string SafeOperation(string? path)
    {
        if (string.Equals(path, ChatGptEndpoints.Session, StringComparison.Ordinal))
        {
            return "session";
        }

        if (string.Equals(path, ChatGptEndpoints.AccountsCheck, StringComparison.Ordinal)
            || string.Equals(path, ChatGptEndpoints.Me, StringComparison.Ordinal))
        {
            return "account-detection";
        }

        if (string.Equals(path, ChatGptEndpoints.Models, StringComparison.Ordinal))
        {
            return "models";
        }

        if (path is not null
            && path.Contains("conversations", StringComparison.OrdinalIgnoreCase))
        {
            return "conversation-index";
        }

        return "request";
    }
}
