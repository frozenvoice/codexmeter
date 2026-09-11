namespace CycleArc.Services;

public static class NotifyIconText
{
    public const int MaximumLength = 127;

    public static string Safe(string? text)
    {
        var normalized = string.IsNullOrWhiteSpace(text)
            ? UiText.ProductName
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var length = Math.Min(normalized.Length, MaximumLength);
        if (length > 0 && char.IsHighSurrogate(normalized[length - 1]))
        {
            length--;
        }

        return normalized[..length];
    }
}
