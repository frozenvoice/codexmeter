namespace CycleArc.Services;

public readonly record struct ConversationSchemaHealthEvidence(
    int AttemptedConversationBodies,
    int SuccessfulConversationBodies,
    int SchemaMismatchConversationCount,
    int TimeoutCount,
    int OtherFailureCount);

public readonly record struct ConversationSchemaHealthAssessment(
    bool SystemicBreak,
    int AttemptedConversationBodies,
    int SuccessfulConversationBodies,
    int SchemaMismatchConversationCount,
    int TimeoutCount,
    int OtherFailureCount);

public static class ConversationSchemaHealthPolicy
{
    public const int MinSystemicSchemaMismatchSamples = 3;
    public const string LatchStateKey = "conversation_schema_systemic_failure";
    public const string LatchParserVersionStateKey = "conversation_schema_systemic_failure_parser_version";

    public static ConversationSchemaHealthAssessment Evaluate(ConversationSchemaHealthEvidence evidence)
    {
        var systemic = evidence.AttemptedConversationBodies > 0
            && evidence.SchemaMismatchConversationCount >= MinSystemicSchemaMismatchSamples
            && evidence.SuccessfulConversationBodies == 0
            && evidence.TimeoutCount == 0
            && evidence.OtherFailureCount == 0
            && evidence.SchemaMismatchConversationCount == evidence.AttemptedConversationBodies;

        return new ConversationSchemaHealthAssessment(
            systemic,
            evidence.AttemptedConversationBodies,
            evidence.SuccessfulConversationBodies,
            evidence.SchemaMismatchConversationCount,
            evidence.TimeoutCount,
            evidence.OtherFailureCount);
    }

    public static bool NextLatch(bool previousLatch, ConversationSchemaHealthAssessment assessment)
    {
        if (assessment.SystemicBreak)
        {
            return true;
        }

        if (assessment.SuccessfulConversationBodies > 0)
        {
            return false;
        }

        return previousLatch;
    }

    public static bool RestoreLatch(string? storedValue, string? storedParserVersion, int currentParserVersion)
    {
        if (!bool.TryParse(storedValue, out var latched) || !latched)
        {
            return false;
        }

        return int.TryParse(storedParserVersion, out var version) && version == currentParserVersion;
    }
}
