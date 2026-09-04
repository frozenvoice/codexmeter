using System.Text.Json.Nodes;
using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class MalformedResponseTests
{
    [Fact]
    public void NullConversation_DoesNotThrow()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var result = parser.Parse(null, new ConversationParseContext());
        Assert.True(result.SchemaMismatch);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void MissingMapping_DoesNotThrow()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var result = parser.Parse(new JsonObject { ["conversation_id"] = "x" }, new ConversationParseContext());
        Assert.True(result.SchemaMismatch);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void InvalidJson_ReturnsNull()
    {
        Assert.Null(ChatGptJson.ParseNode("{not-json"));
        Assert.Null(AccountParser.ParseConversationIndex(new JsonObject { ["oops"] = true }, false).FirstOrDefault());
    }

    [Fact]
    public void AccountParser_SurvivesEmptyObjects()
    {
        var status = AccountParser.ParseSession(new JsonObject());
        Assert.False(status.IsSignedIn);
        Assert.Empty(AccountParser.ParseModels(new JsonObject()));
        Assert.Empty(AccountParser.ParseProjects(new JsonObject()));
        Assert.False(AccountParser.ParseQuotaMetadata(new JsonObject()).IsAuthoritative);
    }
}
