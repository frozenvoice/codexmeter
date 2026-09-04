namespace ProMeter.Services;

public static class SyncFailureClassifier
{
    public static (string Category, int Status) Classify(Exception ex)
    {
        if (ex is ChatGptProviderException provider)
        {
            return (ClassifyProvider(provider), provider.Status);
        }

        return ("Other", 0);
    }

    public static string ClassifyLoad(ConversationLoadResult load)
    {
        if (load.SchemaMismatch)
        {
            return "SchemaMismatch";
        }

        if (load.Diagnostics.Any(ContainsPayloadTooLarge))
        {
            return "PayloadTooLarge";
        }

        if (load.Diagnostics.Any(ContainsTimeout))
        {
            return "BridgeTimeout";
        }

        if (load.Diagnostics.Any(ContainsFallbackExhausted))
        {
            return "EndpointFallbackExhausted";
        }

        if (!load.Complete)
        {
            return "IncompletePagination";
        }

        return "Incomplete";
    }

    private static string ClassifyProvider(ChatGptProviderException provider)
    {
        var message = provider.Message ?? "";
        if (provider.SchemaMismatch || Contains(message, "schema mismatch"))
        {
            return "SchemaMismatch";
        }

        if (ContainsPayloadTooLarge(message))
        {
            return "PayloadTooLarge";
        }

        if (ContainsTimeout(message))
        {
            return "BridgeTimeout";
        }

        if (ContainsFallbackExhausted(message))
        {
            return "EndpointFallbackExhausted";
        }

        if (provider.Status > 0)
        {
            return "HttpStatus";
        }

        return "ProviderError";
    }

    private static bool ContainsPayloadTooLarge(string text) =>
        Contains(text, "PayloadTooLarge") || Contains(text, "payload too large");

    private static bool ContainsTimeout(string text) =>
        Contains(text, "timed out") || Contains(text, "timeout");

    private static bool ContainsFallbackExhausted(string text) =>
        Contains(text, "fallback exhausted") || Contains(text, "endpoint fallback");

    private static bool Contains(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
