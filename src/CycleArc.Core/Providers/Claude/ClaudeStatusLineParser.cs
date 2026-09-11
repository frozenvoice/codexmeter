using System.Text.Json;

namespace CycleArc.Providers.Claude;

public enum ClaudeInputStatus { Available, Missing, Malformed }

public sealed record ClaudeRateLimit(double UsedPercentage, long ResetsAt)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => double.IsFinite(UsedPercentage) && UsedPercentage is >= 0 and <= 100
        && ResetsAt is > 0 and <= 253402300799; // DateTimeOffset's maximum Unix second.
}

public sealed record ClaudeStatusLineResult(ClaudeInputStatus Status, ClaudeRateLimit? FiveHour = null,
    ClaudeRateLimit? SevenDay = null);

/// <summary>Projects only the two documented rate-limit windows from official stdin JSON.</summary>
public static class ClaudeStatusLineParser
{
    public const int MaxInputBytes = 256 * 1024;

    public static ClaudeStatusLineResult Parse(ReadOnlyMemory<byte> input)
    {
        if (input.Length is 0 or > MaxInputBytes) return new(ClaudeInputStatus.Malformed);
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Duplicate(root, "rate_limits")) return new(ClaudeInputStatus.Malformed);
            if (!root.TryGetProperty("rate_limits", out var limits) || limits.ValueKind == JsonValueKind.Null)
                return new(ClaudeInputStatus.Missing);
            if (limits.ValueKind != JsonValueKind.Object || Duplicate(limits, "five_hour") || Duplicate(limits, "seven_day"))
                return new(ClaudeInputStatus.Malformed);
            if (!ReadWindow(limits, "five_hour", out var five) || !ReadWindow(limits, "seven_day", out var week))
                return new(ClaudeInputStatus.Malformed);
            return five is null && week is null ? new(ClaudeInputStatus.Missing)
                : new(ClaudeInputStatus.Available, five, week);
        }
        catch (JsonException) { return new(ClaudeInputStatus.Malformed); }
    }

    private static bool ReadWindow(JsonElement parent, string key, out ClaudeRateLimit? window)
    {
        window = null;
        if (!parent.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return true;
        if (value.ValueKind != JsonValueKind.Object || Duplicate(value, "used_percentage") || Duplicate(value, "resets_at")
            || !value.TryGetProperty("used_percentage", out var used) || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out var percent)
            || !value.TryGetProperty("resets_at", out var resets) || resets.ValueKind != JsonValueKind.Number
            || !resets.TryGetInt64(out var seconds)) return false;
        var parsed = new ClaudeRateLimit(percent, seconds);
        if (!parsed.IsValid) return false;
        window = parsed;
        return true;
    }

    private static bool Duplicate(JsonElement parent, string key) => parent.EnumerateObject().Count(p => p.NameEquals(key)) > 1;
}
