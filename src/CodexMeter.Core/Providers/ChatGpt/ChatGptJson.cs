namespace CodexMeter.Providers.ChatGpt;

public static class ChatGptJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? GetString(JsonNode? node, params string[] names)
    {
        if (node is null)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (node[name] is { } value)
            {
                var text = value.GetValueKind() == JsonValueKind.String
                    ? value.GetValue<string>()
                    : value.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text != "null")
                {
                    return text;
                }
            }
        }

        return null;
    }

    public static double? GetDouble(JsonNode? node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node?[name];
            if (value is null)
            {
                continue;
            }

            try
            {
                return value.GetValue<double>();
            }
            catch
            {
                if (double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    public static bool? GetBool(JsonNode? node, params string[] names)
    {
        foreach (var name in names)
        {
            var value = node?[name];
            if (value is null)
            {
                continue;
            }

            try
            {
                return value.GetValue<bool>();
            }
            catch
            {
                if (bool.TryParse(value.ToString(), out var parsed))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    public static JsonArray? AsArray(JsonNode? node)
    {
        return node as JsonArray;
    }

    public static IEnumerable<JsonNode> Enumerate(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
    }
}
