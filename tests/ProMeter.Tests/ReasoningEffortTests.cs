using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class ReasoningEffortTests
{
    [Theory]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("high", ReasoningEffort.High)]
    [InlineData("extra_high", ReasoningEffort.ExtraHigh)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh)]
    [InlineData("매우 높음", ReasoningEffort.ExtraHigh)]
    [InlineData("unknown-value", ReasoningEffort.Unknown)]
    [InlineData(null, ReasoningEffort.Unknown)]
    public void Normalize_KnownValues(string? raw, ReasoningEffort expected)
    {
        Assert.Equal(expected, ReasoningNormalizer.Normalize(raw));
    }

    [Fact]
    public void Parser_ReadsExtraHighFromMetadata()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var result = parser.Parse(ConversationFixtures.ReasoningMessage(), new ConversationParseContext());
        Assert.Single(result.Events);
        Assert.Equal(ReasoningEffort.ExtraHigh, result.Events[0].ReasoningEffort);
        Assert.Equal(QuotaFamily.SolReasoning, result.Events[0].QuotaFamily);
    }
}
