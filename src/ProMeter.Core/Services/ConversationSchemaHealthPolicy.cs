namespace ProMeter.Services;

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
}
