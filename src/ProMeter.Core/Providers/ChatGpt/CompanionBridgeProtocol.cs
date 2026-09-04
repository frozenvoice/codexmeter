namespace ProMeter.Providers.ChatGpt;

public enum AuthTransportKind
{
    BrowserCompanion,
    WebView2,
    DataExport
}

public sealed class CompanionBridgeMessage
{
    public string? Type { get; set; }
    public string? PairingToken { get; set; }
    public string? RequestId { get; set; }
    public string? Method { get; set; }
    public string? Path { get; set; }
    public string? Body { get; set; }
    public int? Status { get; set; }
    public string? RetryAfter { get; set; }
    public string? Error { get; set; }
    public bool SchemaMismatch { get; set; }
}

public sealed class CompanionParseResult
{
    public bool Accepted { get; init; }
    public bool SchemaMismatch { get; init; }
    public string? Error { get; init; }
    public CompanionBridgeMessage? Message { get; init; }

    public static CompanionParseResult Reject(string error, bool schemaMismatch = true) => new()
    {
        Accepted = false,
        SchemaMismatch = schemaMismatch,
        Error = error
    };
}

public static class CompanionBridgeProtocol
{
    public const string NativeHostName = "com.prometer.bridge";
    public const string Hello = "hello";
    public const string Fetch = "fetch";
    public const string FetchResult = "fetchResult";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(CompanionBridgeMessage message) =>
        JsonSerializer.Serialize(message, JsonOptions);

    public static CompanionParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CompanionParseResult.Reject("empty bridge message");
        }

        var node = ChatGptJson.ParseNode(json);
        if (node is null)
        {
            return CompanionParseResult.Reject("malformed bridge message");
        }

        var type = ChatGptJson.GetString(node, "type", "Type");
        if (string.IsNullOrWhiteSpace(type))
        {
            return CompanionParseResult.Reject("bridge message type missing");
        }

        var message = new CompanionBridgeMessage
        {
            Type = type,
            PairingToken = ChatGptJson.GetString(node, "pairingToken", "pairing_token"),
            RequestId = ChatGptJson.GetString(node, "requestId", "request_id"),
            Method = ChatGptJson.GetString(node, "method"),
            Path = ChatGptJson.GetString(node, "path"),
            Body = ChatGptJson.GetString(node, "body"),
            Status = (int?)ChatGptJson.GetDouble(node, "status"),
            RetryAfter = ChatGptJson.GetString(node, "retryAfter", "retry_after"),
            Error = ChatGptJson.GetString(node, "error"),
            SchemaMismatch = ChatGptJson.GetBool(node, "schemaMismatch", "schema_mismatch") == true
        };

        if (string.Equals(type, Fetch, StringComparison.OrdinalIgnoreCase))
        {
            if (!BackendTargetPolicy.TryValidate(message.Path, out var safe, out var error))
            {
                return CompanionParseResult.Reject(error);
            }

            message.Path = safe;
        }

        return new CompanionParseResult { Accepted = true, Message = message };
    }

    public static ProviderResponse ToProviderResponse(CompanionParseResult parsed)
    {
        if (!parsed.Accepted || parsed.Message is null)
        {
            return new ProviderResponse
            {
                Status = 0,
                Error = parsed.Error ?? "malformed bridge message",
                SchemaMismatch = true
            };
        }

        var message = parsed.Message;
        if (message.SchemaMismatch || message.Status is null)
        {
            return new ProviderResponse
            {
                Status = message.Status ?? 0,
                Error = message.Error ?? "incomplete bridge result",
                SchemaMismatch = true,
                Body = message.Body ?? "",
                RetryAfter = message.RetryAfter
            };
        }

        return new ProviderResponse
        {
            Status = message.Status.Value,
            Body = message.Body ?? "",
            RetryAfter = message.RetryAfter,
            Error = message.Error
        };
    }
}

public static class BrowserResponseSanitizer
{
    public static JsonNode? Sanitize(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var clone = node.DeepClone();
        ConversationDetailLoader.StripBodies(clone);
        StripSecrets(clone);
        return clone;
    }

    public static bool ContainsPromptOrResponseText(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            if (obj["content"] is JsonObject content)
            {
                if (content["parts"] is JsonArray { Count: > 0 } parts
                    && parts.Any(part => part is JsonValue value && value.GetValueKind() == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.ToString())))
                {
                    return true;
                }

                if (content["text"] is JsonValue text
                    && text.GetValueKind() == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(text.ToString()))
                {
                    return true;
                }
            }

            return obj.Any(property => ContainsPromptOrResponseText(property.Value));
        }

        if (node is JsonArray array)
        {
            return array.Any(ContainsPromptOrResponseText);
        }

        return false;
    }

    private static void StripSecrets(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in new[] { "accessToken", "access_token", "sessionToken", "session_token", "authorization", "cookie" })
            {
                obj.Remove(key);
            }

            foreach (var property in obj.ToList())
            {
                StripSecrets(property.Value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                StripSecrets(item);
            }
        }
    }
}

public static class OnboardingOutcomeMapper
{
    public static OnboardingPresentation From(AppSyncStatus status, int used, int limit, string? detail)
    {
        return status switch
        {
            AppSyncStatus.UpToDate => new OnboardingPresentation(
                $"GPT Pro usage: {used} / {limit}",
                ShowCount: true,
                AllowFinish: true,
                AllowRetrySync: false,
                AllowSignInAgain: false),
            AppSyncStatus.PartialData => new OnboardingPresentation(
                $"Partial usage {used} / {limit}. Coverage is incomplete. You can inspect Coverage or retry.",
                ShowCount: true,
                AllowFinish: true,
                AllowRetrySync: true,
                AllowSignInAgain: false),
            AppSyncStatus.AuthenticationRequired => new OnboardingPresentation(
                "Authentication required. Sign in again before a history scan.",
                ShowCount: false,
                AllowFinish: false,
                AllowRetrySync: false,
                AllowSignInAgain: true),
            AppSyncStatus.RateLimited => new OnboardingPresentation(
                "Rate limited. Wait and retry the first manual sync later.",
                ShowCount: false,
                AllowFinish: true,
                AllowRetrySync: true,
                AllowSignInAgain: false),
            AppSyncStatus.ProviderSchemaMismatch => new OnboardingPresentation(
                "Provider schema mismatch. A zero count is not a successful load.",
                ShowCount: false,
                AllowFinish: true,
                AllowRetrySync: true,
                AllowSignInAgain: false),
            AppSyncStatus.Offline => new OnboardingPresentation(
                detail ?? "ChatGPT is unreachable.",
                ShowCount: false,
                AllowFinish: true,
                AllowRetrySync: true,
                AllowSignInAgain: false),
            _ => new OnboardingPresentation(
                detail ?? DisplayStatus(status),
                ShowCount: false,
                AllowFinish: true,
                AllowRetrySync: true,
                AllowSignInAgain: status is AppSyncStatus.SignedOut)
        };
    }

    private static string DisplayStatus(AppSyncStatus status) => status.ToString();
}

public sealed record OnboardingPresentation(
    string Message,
    bool ShowCount,
    bool AllowFinish,
    bool AllowRetrySync,
    bool AllowSignInAgain);
