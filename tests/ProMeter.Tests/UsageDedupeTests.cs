using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class UsageDedupeTests
{
    [Fact]
    public void SameRequestId_CountsOnce()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var result = parser.Parse(ConversationFixtures.MultiAssistantSameRequest(), new ConversationParseContext
        {
            ConversationId = "conv-multi"
        });

        Assert.False(result.SchemaMismatch);
        Assert.Single(result.Events);
        Assert.Equal("req-multi", result.Events[0].RequestId);
        Assert.Equal(QuotaFamily.GptPro, result.Events[0].QuotaFamily);
    }

    [Fact]
    public void DistinctRequestIds_CountSeparately()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var result = parser.Parse(ConversationFixtures.Regenerated(), new ConversationParseContext
        {
            ConversationId = "conv-regen"
        });

        Assert.Equal(3, result.Events.Count);
        Assert.Equal(3, result.Events.Select(e => e.RequestId).Distinct().Count());
        Assert.True(result.HasAlternateBranches);
    }

    [Fact]
    public void ToolCalls_ShareRequestId()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var result = parser.Parse(ConversationFixtures.ToolCalls(), new ConversationParseContext
        {
            ConversationId = "conv-tools"
        });

        Assert.Single(result.Events);
        Assert.Equal("req-tool", result.Events[0].RequestId);
    }

    [Fact]
    public void HiddenReasoningToolAndFinal_WithoutRequestId_CountOnce()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var result = parser.Parse(ConversationFixtures.MissingRequestIdFragments(), new ConversationParseContext
        {
            ConversationId = "conv-noreq-cluster"
        });

        Assert.False(result.SchemaMismatch);
        Assert.Single(result.Events);
        Assert.Equal(DedupeConfidence.Heuristic, result.Events[0].DedupeConfidence);
        Assert.StartsWith("turn:", result.Events[0].DedupeKey);
        Assert.Equal("final", result.Events[0].MessageId);
    }
}
