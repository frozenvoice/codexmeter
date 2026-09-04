using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class FixtureCasesTests
{
    [Fact]
    public void AllSyntheticCases_ParseWithoutCrash()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var cases = new (string Name, JsonNode Node, int Expected)[]
        {
            ("normal Pro response", ConversationFixtures.NormalPro(), 1),
            ("multiple assistant nodes same request_id", ConversationFixtures.MultiAssistantSameRequest(), 1),
            ("regenerated response", ConversationFixtures.Regenerated(), 3),
            ("tool calls", ConversationFixtures.ToolCalls(), 1),
            ("reasoning message", ConversationFixtures.ReasoningMessage(), 1),
            ("project conversation", ConversationFixtures.ProjectConversation(), 1),
            ("archived conversation", ConversationFixtures.ArchivedConversation(), 1),
            ("unknown model", ConversationFixtures.UnknownModel(), 1),
            ("missing request_id", ConversationFixtures.MissingRequestId(), 1),
            ("missing request_id fragments", ConversationFixtures.MissingRequestIdFragments(), 1),
            ("gpt-6 pro", ConversationFixtures.Gpt6Pro(), 1),
            ("long conversation", ConversationFixtures.LongConversation(), 24)
        };

        foreach (var (name, node, expected) in cases)
        {
            var result = parser.Parse(node, new ConversationParseContext { ConversationId = name });
            Assert.False(result.SchemaMismatch, name);
            Assert.Equal(expected, result.Events.Count);
        }

        Assert.Equal(QuotaFamily.Unknown, parser.Parse(ConversationFixtures.UnknownModel(), new ConversationParseContext()).Events[0].QuotaFamily);
        Assert.False(string.IsNullOrWhiteSpace(parser.Parse(ConversationFixtures.MissingRequestId(), new ConversationParseContext()).Events[0].DedupeKey));
    }
}
