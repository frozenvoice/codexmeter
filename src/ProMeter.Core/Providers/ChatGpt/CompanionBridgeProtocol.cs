using System.Text;

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
    public string? Operation { get; set; }
    public CompanionOperationArgs? Args { get; set; }
    public int? Status { get; set; }
    public string? RetryAfter { get; set; }
    public string? Body { get; set; }
    public string? Error { get; set; }
    public bool SchemaMismatch { get; set; }
    public bool PayloadTooLarge { get; set; }
    public bool Accepted { get; set; }
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
    public const string HelloAck = "helloAck";
    public const string Invoke = "invoke";
    public const string InvokeResult = "invokeResult";
    public const string ErrorType = "error";
    public const int MaxNativeMessageBytes = 1_048_576;
    public const int MaxCommandBytes = 16_384;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(60);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(CompanionBridgeMessage message) =>
        JsonSerializer.Serialize(message, JsonOptions);

    public static CompanionParseResult Parse(string? json, int maxBytes = MaxNativeMessageBytes)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return CompanionParseResult.Reject("empty bridge message");
        }

        if (Encoding.UTF8.GetByteCount(json) > maxBytes)
        {
            return CompanionParseResult.Reject("PayloadTooLarge", schemaMismatch: false);
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

        if (string.Equals(type, "fetch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "fetchResult", StringComparison.OrdinalIgnoreCase))
        {
            return CompanionParseResult.Reject("generic fetch is forbidden");
        }

        var message = new CompanionBridgeMessage
        {
            Type = type,
            PairingToken = ChatGptJson.GetString(node, "pairingToken", "pairing_token"),
            RequestId = ChatGptJson.GetString(node, "requestId", "request_id"),
            Operation = ChatGptJson.GetString(node, "operation"),
            Status = (int?)ChatGptJson.GetDouble(node, "status"),
            RetryAfter = ChatGptJson.GetString(node, "retryAfter", "retry_after"),
            Body = ChatGptJson.GetString(node, "body"),
            Error = ChatGptJson.GetString(node, "error"),
            SchemaMismatch = ChatGptJson.GetBool(node, "schemaMismatch", "schema_mismatch") == true,
            PayloadTooLarge = ChatGptJson.GetBool(node, "payloadTooLarge", "payload_too_large") == true,
            Accepted = ChatGptJson.GetBool(node, "accepted") == true
        };

        if (node["args"] is JsonObject argsNode)
        {
            message.Args = new CompanionOperationArgs
            {
                ConversationId = ChatGptJson.GetString(argsNode, "conversationId", "conversation_id"),
                ProjectId = ChatGptJson.GetString(argsNode, "projectId", "project_id"),
                Cursor = ChatGptJson.GetString(argsNode, "cursor"),
                Offset = (int?)ChatGptJson.GetDouble(argsNode, "offset"),
                Limit = (int?)ChatGptJson.GetDouble(argsNode, "limit"),
                Archived = ChatGptJson.GetBool(argsNode, "archived"),
                Body = ChatGptJson.GetString(argsNode, "body")
            };
        }

        if (string.Equals(type, Invoke, StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<CompanionOperation>(message.Operation, ignoreCase: true, out var operation)
                || !Enum.IsDefined(operation))
            {
                return CompanionParseResult.Reject("unknown operation");
            }

            if (!CompanionOperationRouter.TryBuild(operation, message.Args, out _, out _, out _, out var error))
            {
                return CompanionParseResult.Reject(error);
            }
        }

        return new CompanionParseResult { Accepted = true, Message = message };
    }

    public static ProviderResponse ToProviderResponse(CompanionParseResult parsed, CompanionOperation? operation = null)
    {
        if (parsed.Error == "PayloadTooLarge")
        {
            return new ProviderResponse { Status = 0, Error = "PayloadTooLarge", SchemaMismatch = true };
        }

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
        if (message.PayloadTooLarge)
        {
            return new ProviderResponse { Status = 0, Error = "PayloadTooLarge", SchemaMismatch = true };
        }

        if (message.Status is 401 or 403 or 429 || message.Status is >= 500 and < 600)
        {
            return new ProviderResponse
            {
                Status = message.Status.Value,
                RetryAfter = message.RetryAfter,
                Body = message.Body ?? "",
                Error = message.Error,
                SchemaMismatch = message.SchemaMismatch
            };
        }

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

        if (operation is CompanionOperation op && !string.IsNullOrWhiteSpace(message.Body))
        {
            var node = ChatGptJson.ParseNode(message.Body);
            if (node is null && message.Status is >= 200 and < 300)
            {
                return new ProviderResponse
                {
                    Status = 0,
                    Error = "projected body was not JSON",
                    SchemaMismatch = true
                };
            }

            if (node is not null && !BridgeProjection.TryValidateProjected(op, node, out var leak))
            {
                return new ProviderResponse { Status = 0, Error = leak, SchemaMismatch = true };
            }
        }

        return new ProviderResponse
        {
            Status = message.Status.Value,
            Body = message.Body ?? "",
            RetryAfter = message.RetryAfter,
            Error = message.Error
        };
    }

    public static ProviderResponse TimeoutResponse() =>
        new() { Status = 0, Error = "bridge request timed out" };

    public static ProviderResponse DisconnectResponse() =>
        new() { Status = 0, Error = "browser companion disconnected" };

    public static ProviderResponse WriteFailureResponse() =>
        new() { Status = 0, Error = "bridge write failed" };

    public static ProviderResponse OperationMismatchResponse() =>
        new() { Status = 0, Error = "bridge result operation mismatch", SchemaMismatch = true };
}

public static class OnboardingOutcomeMapper
{
    public static OnboardingPresentation From(AppSyncStatus status, int used, int limit, string? detail, bool usageUnavailable = false)
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
                usageUnavailable
                    ? $"GPT Pro: ? / {limit}. Incomplete reconstruction."
                    : $"Partial usage {used} / {limit}. Coverage is incomplete. You can inspect Coverage or retry.",
                ShowCount: !usageUnavailable,
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
                usageUnavailable
                    ? $"GPT Pro: ? / {limit}. Incomplete reconstruction."
                    : "Provider schema mismatch. A zero count is not a successful load.",
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
                detail ?? status.ToString(),
                ShowCount: false,
                AllowFinish: true,
                AllowRetrySync: true,
                AllowSignInAgain: status is AppSyncStatus.SignedOut)
        };
    }
}

public sealed record OnboardingPresentation(
    string Message,
    bool ShowCount,
    bool AllowFinish,
    bool AllowRetrySync,
    bool AllowSignInAgain);
