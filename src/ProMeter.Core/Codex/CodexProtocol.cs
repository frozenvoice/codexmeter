using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProMeter.Codex;

public static class CodexProtocol
{
    public const int InitializeId = 1;
    public const int AccountReadId = 2;
    public const int RateLimitsReadId = 3;
    public const int MaxJsonLineBytes = 262_144;
    public const int MaxStderrBytes = 4_096;
    public const int ProcessStartTimeoutMs = 5_000;
    public const int InitializeTimeoutMs = 10_000;
    public const int AccountReadTimeoutMs = 10_000;
    public const int RateLimitsReadTimeoutMs = 10_000;
    public const int GracefulShutdownTimeoutMs = 2_000;
    public const int TotalHardCeilingMs = 30_000;

    private static readonly HashSet<string> ForbiddenMethods = new(StringComparer.Ordinal)
    {
        "thread/start",
        "thread/resume",
        "turn/start",
        "turn/interrupt"
    };

    public static string BuildInitialize(string version)
    {
        var node = new JsonObject
        {
            ["method"] = "initialize",
            ["id"] = InitializeId,
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "prometer",
                    ["title"] = "ProMeter",
                    ["version"] = version
                },
                ["capabilities"] = new JsonObject
                {
                    ["optOutNotificationMethods"] = new JsonArray(
                        "thread/started",
                        "turn/started",
                        "item/started",
                        "item/completed",
                        "item/agentMessage/delta")
                }
            }
        };
        return node.ToJsonString();
    }

    public static string BuildInitialized() =>
        new JsonObject
        {
            ["method"] = "initialized",
            ["params"] = new JsonObject()
        }.ToJsonString();

    public static string BuildAccountRead() =>
        new JsonObject
        {
            ["method"] = "account/read",
            ["id"] = AccountReadId
        }.ToJsonString();

    public static string BuildRateLimitsRead() =>
        new JsonObject
        {
            ["method"] = "account/rateLimits/read",
            ["id"] = RateLimitsReadId
        }.ToJsonString();

    public static bool IsForbiddenMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return false;
        }

        return ForbiddenMethods.Contains(method)
               || method.StartsWith("thread/", StringComparison.Ordinal)
               || method.StartsWith("turn/", StringComparison.Ordinal)
               || method.Contains("item/started", StringComparison.Ordinal);
    }

    public static bool TryGetResponseId(JsonNode? node, out string? id)
    {
        id = null;
        if (node is not JsonObject obj || !obj.TryGetPropertyValue("id", out var raw) || raw is null)
        {
            return false;
        }

        id = raw switch
        {
            JsonValue value when value.TryGetValue<int>(out var n) => n.ToString(),
            JsonValue value when value.TryGetValue<long>(out var n) => n.ToString(),
            JsonValue value when value.TryGetValue<string>(out var s) => s,
            _ => raw.ToString()
        };
        return !string.IsNullOrWhiteSpace(id);
    }

    public static bool IsNotification(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return false;
        }

        return !obj.ContainsKey("id") && obj.TryGetPropertyValue("method", out _);
    }

    public static bool HasError(JsonNode? node) =>
        node is JsonObject obj && obj.TryGetPropertyValue("error", out var error) && error is not null;

    public static string SanitizeDiagnostic(string? text, int maxBytes = MaxStderrBytes)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sanitized = text
            .Replace("Authorization", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("Bearer ", "[redacted] ", StringComparison.OrdinalIgnoreCase)
            .Replace("access_token", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("refresh_token", "[redacted]", StringComparison.OrdinalIgnoreCase)
            .Replace("auth.json", "[redacted]", StringComparison.OrdinalIgnoreCase);

        var utf8 = System.Text.Encoding.UTF8;
        var bytes = utf8.GetBytes(sanitized);
        if (bytes.Length <= maxBytes)
        {
            return sanitized;
        }

        return utf8.GetString(bytes, 0, maxBytes) + "…";
    }

    public static JsonNode? ParseLine(string line)
    {
        if (line.Length > MaxJsonLineBytes)
        {
            throw new CodexProtocolException("JSONL line exceeded the safe maximum size.");
        }

        return JsonNode.Parse(line);
    }
}

public sealed class CodexProtocolException : Exception
{
    public CodexProtocolException(string message) : base(message)
    {
    }
}
