namespace ProMeter.Services;

public enum WebViewDiagnosticStatus
{
    Pass,
    FailAuth,
    FailSession,
    FailApi,
    Forbidden,
    Cancelled
}

public enum WebViewInteractiveLoginResult
{
    SignedIn,
    Cancelled,
    Unsupported
}

public interface IWebViewInteractiveLogin
{
    Task<WebViewInteractiveLoginResult> ShowInteractiveLoginAsync(CancellationToken cancellationToken = default);
}

public interface IWebViewDiagnosticHost
{
    void RealizeDiagnosticHost();
    void HideDiagnosticHost();
    void PrepareLoginOnSameHost();
}

public sealed record WebViewDiagnosticResult(
    WebViewDiagnosticStatus Status,
    int? HttpStatus = null,
    string? TechnicalDetail = null)
{
    public bool Passed => Status == WebViewDiagnosticStatus.Pass;
}

/// <summary>
/// Performs metadata-only WebView2 compatibility checks. This service has no
/// settings or persistence dependency, so it cannot change transport choices,
/// startup preferences, sync preferences, usage events, or scan watermarks.
/// </summary>
public sealed class WebViewCompatibilityDiagnostic
{
    private readonly IChatGptTransport _transport;
    private readonly IWebViewInteractiveLogin _login;
    private readonly IWebViewDiagnosticHost? _host;
    private readonly Action<string>? _logStage;

    public WebViewCompatibilityDiagnostic(IChatGptTransport transport, IWebViewInteractiveLogin login)
        : this(transport, login, host: null, logStage: null)
    {
    }

    public WebViewCompatibilityDiagnostic(
        IChatGptTransport transport,
        IWebViewInteractiveLogin login,
        IWebViewDiagnosticHost? host,
        Action<string>? logStage = null)
    {
        _transport = transport;
        _login = login;
        _host = host;
        _logStage = logStage;
    }

    public async Task<WebViewDiagnosticResult> RunAsync(CancellationToken cancellationToken = default)
    {
        _logStage?.Invoke(WebViewDiagnosticStages.InitializeStart);
        _host?.RealizeDiagnosticHost();
        try
        {
            var session = await SendAsync(ChatGptEndpoints.Session, cancellationToken);
            if (session.IsForbidden)
            {
                return Forbidden("session", session.Status);
            }

            var signedIn = TryReadSignedInSession(session, out var sessionShapeValid);
            if (!signedIn)
            {
                if (session.SchemaMismatch || (session.IsSuccess && !sessionShapeValid))
                {
                    return FailApi("session", session.Status, "schema-mismatch");
                }

                if (!session.IsUnauthorized && !session.IsSuccess)
                {
                    return FailApi("session", session.Status);
                }

                WebViewInteractiveLoginResult login;
                try
                {
                    _host?.PrepareLoginOnSameHost();
                    login = await _login.ShowInteractiveLoginAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Cancelled);
                }
                catch
                {
                    return new WebViewDiagnosticResult(
                        WebViewDiagnosticStatus.FailAuth,
                        TechnicalDetail: UiText.WebViewDiagnosticTechnical("interactive-login", reason: "unavailable"));
                }

                if (login == WebViewInteractiveLoginResult.Cancelled)
                {
                    return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Cancelled);
                }

                if (login == WebViewInteractiveLoginResult.Unsupported)
                {
                    return new WebViewDiagnosticResult(
                        WebViewDiagnosticStatus.FailAuth,
                        TechnicalDetail: UiText.WebViewDiagnosticTechnical("interactive-login", reason: "unsupported"));
                }

                session = await SendAsync(ChatGptEndpoints.Session, cancellationToken);
                if (session.IsForbidden)
                {
                    return Forbidden("session", session.Status);
                }

                if (!TryReadSignedInSession(session, out _))
                {
                    return new WebViewDiagnosticResult(
                        WebViewDiagnosticStatus.FailSession,
                        SafeStatus(session),
                        Detail("session-verification", session));
                }
            }

            var failure = await ProbeAccountAsync(cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            var models = await SendAsync(ChatGptEndpoints.Models, cancellationToken);
            failure = ValidateJsonResponse(models, "models", IsModelsShape);
            if (failure is not null)
            {
                return failure;
            }

            // Deliberately request one normal-chat index page directly. Do not use
            // ChatGptProvider.FetchIndexAsync here because that follows pagination.
            var index = await SendAsync(
                ChatGptEndpoints.ConversationsPage(0, ConversationIndexPager.RequestedLimit, archived: false),
                cancellationToken);
            failure = ValidateIndexResponse(index);
            if (failure is not null)
            {
                return failure;
            }

            return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Pass);
        }
        catch (WebViewDiagnosticInitializationException ex)
        {
            return ex.Result;
        }
        catch (OperationCanceledException)
        {
            _logStage?.Invoke(WebViewDiagnosticStages.Cancelled);
            return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Cancelled);
        }
        catch
        {
            return new WebViewDiagnosticResult(
                WebViewDiagnosticStatus.FailApi,
                TechnicalDetail: UiText.WebViewDiagnosticTechnical("diagnostic", reason: "unavailable"));
        }
        finally
        {
            _host?.HideDiagnosticHost();
        }
    }

    private async Task<ProviderResponse> SendAsync(string path, CancellationToken cancellationToken)
    {
        if (string.Equals(path, ChatGptEndpoints.Session, StringComparison.Ordinal))
        {
            _logStage?.Invoke(WebViewDiagnosticStages.SessionProbe);
        }

        var response = await _transport.SendAsync("GET", path, cancellationToken: cancellationToken);
        if (MapInitializationFailure(response) is { } failure)
        {
            if (response.Error is WebViewInitializationCodes.RequestTimeout
                or WebViewInitializationCodes.ScriptExecutionTimeout)
            {
                _logStage?.Invoke(WebViewDiagnosticStages.RequestTimeoutFor(WebViewFetchScript.SafeOperation(path)));
            }

            throw new WebViewDiagnosticInitializationException(failure);
        }

        return response;
    }

    public static WebViewDiagnosticResult? MapInitializationFailure(ProviderResponse response)
    {
        if (string.IsNullOrWhiteSpace(response.Error))
        {
            return null;
        }

        if (response.Error == WebViewInitializationCodes.Timeout)
        {
            return new WebViewDiagnosticResult(
                WebViewDiagnosticStatus.FailApi,
                TechnicalDetail: UiText.WebViewInitializationTimedOut);
        }

        if (response.Error == WebViewInitializationCodes.NavigationTimeout)
        {
            return new WebViewDiagnosticResult(
                WebViewDiagnosticStatus.FailApi,
                TechnicalDetail: UiText.WebViewNavigationTimedOut);
        }

        if (response.Error == WebViewInitializationCodes.NavigationFailed)
        {
            return new WebViewDiagnosticResult(
                WebViewDiagnosticStatus.FailApi,
                TechnicalDetail: UiText.WebViewNavigationFailed);
        }

        if (response.Error is WebViewInitializationCodes.RequestTimeout
            or WebViewInitializationCodes.ScriptExecutionTimeout)
        {
            return new WebViewDiagnosticResult(
                WebViewDiagnosticStatus.FailApi,
                TechnicalDetail: UiText.WebViewRequestTimedOut);
        }

        if (response.Error == WebViewInitializationCodes.RequestFailed)
        {
            return new WebViewDiagnosticResult(
                WebViewDiagnosticStatus.FailApi,
                TechnicalDetail: UiText.WebViewRequestFailed);
        }

        if (response.Error is WebViewInitializationCodes.RuntimeUnavailable
            or WebViewInitializationCodes.HostInvalid
            or WebViewInitializationCodes.EnvironmentFailed)
        {
            return new WebViewDiagnosticResult(
                WebViewDiagnosticStatus.FailApi,
                TechnicalDetail: UiText.WebViewInitializationFailed);
        }

        if (response.Error == WebViewInitializationCodes.Cancelled)
        {
            return new WebViewDiagnosticResult(WebViewDiagnosticStatus.Cancelled);
        }

        return null;
    }

    private async Task<WebViewDiagnosticResult?> ProbeAccountAsync(CancellationToken cancellationToken)
    {
        var account = await SendAsync(ChatGptEndpoints.AccountsCheck, cancellationToken);
        if (account.IsSuccess || account.SchemaMismatch || account.IsUnauthorized
            || account.IsForbidden || account.IsRateLimited || account.IsOffline)
        {
            return ValidateJsonResponse(account, "account-detection", IsAccountShape);
        }

        // Match the normal provider's safe account-detection fallback without
        // expanding the diagnostic into any history scan.
        var me = await SendAsync(ChatGptEndpoints.Me, cancellationToken);
        return ValidateJsonResponse(me, "account-detection", IsAccountShape);
    }

    private static bool TryReadSignedInSession(ProviderResponse response, out bool shapeValid)
    {
        shapeValid = false;
        if (!response.IsSuccess || response.SchemaMismatch || string.IsNullOrWhiteSpace(response.Body))
        {
            return false;
        }

        var root = ChatGptJson.ParseNode(response.Body);
        shapeValid = root is JsonObject;
        return shapeValid && AccountParser.ParseSession(root).IsSignedIn;
    }

    private static WebViewDiagnosticResult? ValidateJsonResponse(
        ProviderResponse response,
        string operation,
        Func<JsonNode?, bool> shapeValidator)
    {
        if (response.IsForbidden)
        {
            return Forbidden(operation, response.Status);
        }

        if (!response.IsSuccess || response.SchemaMismatch)
        {
            return FailApi(operation, response.Status, response.SchemaMismatch ? "schema-mismatch" : null);
        }

        var root = ChatGptJson.ParseNode(response.Body);
        return shapeValidator(root)
            ? null
            : FailApi(operation, response.Status, "schema-mismatch");
    }

    private static WebViewDiagnosticResult? ValidateIndexResponse(ProviderResponse response)
    {
        var generic = ValidateJsonResponse(response, "conversation-index", root => root is not null);
        if (generic is not null)
        {
            return generic;
        }

        var parsed = AccountParser.ParseConversationIndex(
            ChatGptJson.ParseNode(response.Body),
            archived: false,
            source: "diagnostic");
        if (!parsed.RecognizedShape || parsed.SchemaMismatch)
        {
            return FailApi("conversation-index", response.Status, "schema-mismatch");
        }

        if (parsed.Incomplete || !parsed.TimestampComplete)
        {
            return FailApi("conversation-index", response.Status, "incomplete-metadata");
        }

        return null;
    }

    private static bool IsAccountShape(JsonNode? root) => root is JsonObject;

    private static bool IsModelsShape(JsonNode? root) =>
        root is JsonArray
        || root?["models"] is JsonArray
        || root?["categories"] is JsonArray;

    private static WebViewDiagnosticResult Forbidden(string operation, int status) =>
        new(WebViewDiagnosticStatus.Forbidden, status, UiText.WebViewDiagnosticTechnical(operation, status));

    private static WebViewDiagnosticResult FailApi(string operation, int status, string? reason = null) =>
        new(WebViewDiagnosticStatus.FailApi, SafeStatus(status), Detail(operation, status, reason));

    private static int? SafeStatus(ProviderResponse response) => SafeStatus(response.Status);

    private static int? SafeStatus(int status) => status > 0 ? status : null;

    private static string Detail(string operation, ProviderResponse response) =>
        Detail(operation, response.Status, response.SchemaMismatch ? "schema-mismatch" : null);

    private static string Detail(string operation, int status, string? reason = null)
        => UiText.WebViewDiagnosticTechnical(operation, status, reason);
}

internal sealed class WebViewDiagnosticInitializationException : Exception
{
    public WebViewDiagnosticInitializationException(WebViewDiagnosticResult result)
        : base(result.Status.ToString())
    {
        Result = result;
    }

    public WebViewDiagnosticResult Result { get; }
}
