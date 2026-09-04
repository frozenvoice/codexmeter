namespace ProMeter.Providers.ChatGpt;

public static class ReasoningNormalizer
{
    public static ReasoningEffort Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ReasoningEffort.Unknown;
        }

        var key = value.Trim().ToLowerInvariant()
            .Replace('_', '-')
            .Replace(' ', '-');

        return key switch
        {
            "none" or "off" or "instant" or "fast" => ReasoningEffort.None,
            "low" or "minimal" or "light" => ReasoningEffort.Low,
            "medium" or "standard" or "default" or "보통" => ReasoningEffort.Medium,
            "high" or "높음" => ReasoningEffort.High,
            "extra-high" or "extrahigh" or "extra_high" or "xhigh" or "x-high"
                or "extended" or "max" or "very-high" or "매우-높음" or "매우높음" => ReasoningEffort.ExtraHigh,
            _ => ReasoningEffort.Unknown
        };
    }

    public static ReasoningEffort FromMetadata(JsonNode? metadata)
    {
        if (metadata is null)
        {
            return ReasoningEffort.Unknown;
        }

        var raw = ChatGptJson.GetString(
            metadata,
            "reasoning_effort",
            "thinking_effort",
            "effort",
            "reasoningEffort",
            "thinkingEffort");

        if (string.IsNullOrWhiteSpace(raw) && metadata["model_experience"] is JsonNode experience)
        {
            raw = ChatGptJson.GetString(experience, "reasoning_effort", "thinking_effort", "effort");
        }

        return Normalize(raw);
    }

    public static string ToStorage(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => "none",
        ReasoningEffort.Low => "low",
        ReasoningEffort.Medium => "medium",
        ReasoningEffort.High => "high",
        ReasoningEffort.ExtraHigh => "extra_high",
        _ => "unknown"
    };

    public static ReasoningEffort FromStorage(string? value) => Normalize(value);

    public static string ToDisplay(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => "None",
        ReasoningEffort.Low => "Low",
        ReasoningEffort.Medium => "Medium",
        ReasoningEffort.High => "High",
        ReasoningEffort.ExtraHigh => "Extra High",
        _ => "Unknown"
    };
}
