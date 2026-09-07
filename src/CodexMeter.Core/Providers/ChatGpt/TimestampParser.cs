namespace CodexMeter.Providers.ChatGpt;

public static class TimestampParser
{
    public static DateTimeOffset? ToDateTimeOffset(JsonNode? node, params string[] names)
    {
        if (node is null)
        {
            return null;
        }

        if (names.Length == 0)
        {
            return Parse(node);
        }

        foreach (var name in names)
        {
            var parsed = Parse(node[name]);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        return null;
    }

    public static DateTimeOffset? Parse(JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            switch (value.GetValueKind())
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Number:
                    return FromUnixNumber(value.GetValue<double>());
                case JsonValueKind.String:
                    return Parse(value.GetValue<string>());
                default:
                    return Parse(value.ToString());
            }
        }
        catch
        {
            return Parse(value.ToString());
        }
    }

    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "null" or "undefined")
        {
            return null;
        }

        var text = value.Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return FromUnixNumber(number);
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    public static double ToUnixSeconds(DateTimeOffset? value) =>
        value is DateTimeOffset stamp ? stamp.ToUnixTimeSeconds() : 0;

    public static double ToUnixSeconds(JsonNode? node, params string[] names) =>
        ToUnixSeconds(ToDateTimeOffset(node, names));

    public static DateTimeOffset? FromUnixNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return null;
        }

        try
        {
            if (value >= 1_000_000_000_000d)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)value);
            }

            if (value >= 1_000_000_000d)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000d));
            }

            return DateTimeOffset.FromUnixTimeSeconds((long)value);
        }
        catch
        {
            return null;
        }
    }
}
