namespace CycleArc.Services;

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
        if (load.FailureKind == ConversationLoadFailureKind.PayloadTooLarge
            || load.Diagnostics.Any(ContainsPayloadTooLarge))
        {
            return "PayloadTooLarge";
        }

        if (load.FailureKind == ConversationLoadFailureKind.Timeout
            || load.Diagnostics.Any(ContainsTimeout))
        {
            return "BridgeTimeout";
        }

        if (load.FailureKind == ConversationLoadFailureKind.SchemaMismatch
            || load.SchemaMismatch)
        {
            return "SchemaMismatch";
        }

        if (load.FailureKind == ConversationLoadFailureKind.EndpointUnavailable
            || load.Diagnostics.Any(ContainsFallbackExhausted)
            || load.Diagnostics.Any(item => item.Contains("unavailable status=", StringComparison.OrdinalIgnoreCase)))
        {
            return "EndpointFallbackExhausted";
        }

        if (load.FailureKind == ConversationLoadFailureKind.IncompletePagination || !load.Complete)
        {
            return "IncompletePagination";
        }

        return "Incomplete";
    }

    private static string ClassifyProvider(ChatGptProviderException provider)
    {
        var message = provider.Message ?? "";
        if (provider.IsPayloadTooLarge || ContainsPayloadTooLarge(message))
        {
            return "PayloadTooLarge";
        }

        if (provider.SchemaMismatch || Contains(message, "schema mismatch"))
        {
            return "SchemaMismatch";
        }

        if (provider.IsCompanionDisconnected)
        {
            return "CompanionDisconnected";
        }

        if (provider.IsBridgeWriteFailed)
        {
            return "BridgeWriteFailed";
        }

        if (ContainsTimeout(message) || provider.IsBridgeTimeout)
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
