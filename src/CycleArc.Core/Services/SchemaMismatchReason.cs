using System.Text.RegularExpressions;

namespace CycleArc.Services;

public static class SchemaMismatchReason
{
    public const string GenericMessage = "Provider schema mismatch";
    public const int MaxDisplayLength = 80;

    private static readonly Regex GuidLike = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly string[] Known =
    [
        "conversation collection too large",
        "mapping too large",
        "children rejected",
        "children must be an array",
        "metadata field rejected",
        "metadata value rejected",
        "metadata must be an object",
        "timestamp rejected",
        "unknown projected field",
        "too many fields",
        "expected object",
        "mapping must be an object",
        "projected body was not JSON",
        "projected body missing",
        "secret field leaked through bridge"
    ];

    public static string ExceptionMessage(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return GenericMessage;
        }

        var sanitized = AppLog.Sanitize(error.Trim());
        return LooksLikeRawPayload(sanitized) ? GenericMessage : sanitized;
    }

    public static string? Normalize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var sanitized = AppLog.Sanitize(error).Trim();
        if (LooksLikeRawPayload(sanitized))
        {
            return null;
        }

        if (sanitized.Contains("unexpected field", StringComparison.OrdinalIgnoreCase))
        {
            return "unknown projected field";
        }

        foreach (var known in Known)
        {
            if (sanitized.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        if (sanitized.Contains("schema mismatch", StringComparison.OrdinalIgnoreCase)
            && sanitized.Length <= GenericMessage.Length + 8)
        {
            return null;
        }

        sanitized = GuidLike.Replace(sanitized, "").Trim(' ', '-', ':', ';', ',');
        if (string.IsNullOrWhiteSpace(sanitized) || LooksUnsafe(sanitized))
        {
            return null;
        }

        return sanitized.Length <= MaxDisplayLength
            ? sanitized
            : sanitized[..MaxDisplayLength].TrimEnd() + "…";
    }

    public static bool LooksLikeRawPayload(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static bool LooksUnsafe(string text) =>
        text.Contains('{')
        || text.Contains('[')
        || text.Contains('"')
        || text.Contains("Bearer", StringComparison.OrdinalIgnoreCase)
        || text.Contains("accessToken", StringComparison.OrdinalIgnoreCase)
        || text.Contains("http", StringComparison.OrdinalIgnoreCase);
}
