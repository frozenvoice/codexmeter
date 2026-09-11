using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

public class DirectPaginatedProjectionTests
{
    [Theory]
    [InlineData("gpt-5-6-pro", "req-direct-pro")]
    [InlineData("gpt-6-pro", "req-direct-gpt6")]
    public async Task DirectPaginatedAssistant_SurvivesProjectionValidationAndParse(string slug, string requestId)
    {
        var raw = ConversationFixtures.DirectPaginatedPro("conv-direct", 1_777_500_900, slug, requestId);
        var rawJson = raw.ToJsonString();
        Assert.Contains("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE", rawJson, StringComparison.Ordinal);
        Assert.Contains("SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE", rawJson, StringComparison.Ordinal);

        var projected = BridgeProjection.Project(CompanionOperation.GetConversationFull, raw);
        Assert.NotNull(projected);
        Assert.True(BridgeProjection.TryValidateProjected(CompanionOperation.GetConversationFull, projected, out var error), error);
        Assert.False(BridgeProjection.ContainsPromptOrResponseText(projected));

        var projectedJson = projected!.ToJsonString();
        Assert.DoesNotContain("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE", projectedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE", projectedJson, StringComparison.Ordinal);

        var messages = projected["messages"] as JsonArray;
        Assert.NotNull(messages);
        Assert.Equal(2, messages!.Count);
        var assistant = messages.Single(node =>
            node?["message"]?["author"]?["role"]?.GetValue<string>() == "assistant");
        Assert.Equal("assistant", assistant?["message"]?["author"]?["role"]?.GetValue<string>());
        Assert.Equal(requestId, assistant?["message"]?["metadata"]?["request_id"]?.GetValue<string>());
        Assert.Equal(slug, assistant?["message"]?["metadata"]?["model_slug"]?.GetValue<string>());
        Assert.Null(assistant?["message"]?["content"]?["parts"]);
        Assert.Null(assistant?["message"]?["content"]?["text"]);

        var load = await new ConversationDetailLoader().LoadPaginatedAsync(
            "conv-direct",
            (_, _, _, _) => Task.FromResult<JsonNode?>(null),
            projected,
            [],
            CancellationToken.None);
        Assert.True(load.Complete, string.Join("; ", load.Diagnostics));
        Assert.False(load.SchemaMismatch);

        var parsed = new ConversationParser(new ModelNormalizer()).Parse(
            load.Conversation,
            new ConversationParseContext { ConversationId = "conv-direct" });
        Assert.False(parsed.SchemaMismatch);
        Assert.True(parsed.AssistantLikeNodeCount >= 1);
        Assert.True(parsed.NodesWithModelMetadata >= 1);
        Assert.Single(parsed.Events);
        Assert.Equal(QuotaFamily.GptPro, parsed.Events[0].QuotaFamily);
        Assert.Equal(slug, parsed.Events[0].RawModel);
        Assert.Equal(requestId, parsed.Events[0].RequestId);
    }

    [Fact]
    public void DirectPaginatedAliases_AreCanonicalizedIntoMappingNodes()
    {
        var raw = new JsonObject
        {
            ["conversation_id"] = "conv-alias",
            ["current_node"] = "asst-1",
            ["messages"] = new JsonArray
            {
                ConversationFixtures.DirectPaginatedMessage("user-1", "user", null, 1_777_500_901, useAliases: true),
                ConversationFixtures.DirectPaginatedMessage(
                    "asst-1",
                    "assistant",
                    "user-1",
                    1_777_500_902,
                    requestId: "req-alias",
                    modelSlug: "gpt-5-6-pro",
                    useAliases: true)
            }
        };

        var projected = BridgeProjection.Project(CompanionOperation.GetOlderConversationMessages, raw);
        Assert.NotNull(projected);
        Assert.True(BridgeProjection.TryValidateProjected(CompanionOperation.GetOlderConversationMessages, projected, out var error), error);
        var assistant = projected!["messages"]![1]!;
        Assert.Equal("asst-1", assistant["id"]?.GetValue<string>());
        Assert.Equal("user-1", assistant["parent"]?.GetValue<string>());
        Assert.Null(assistant["message_id"]);
        Assert.Null(assistant["parent_id"]);
        Assert.Equal("assistant", assistant["message"]?["author"]?["role"]?.GetValue<string>());
        Assert.Equal("req-alias", assistant["message"]?["metadata"]?["request_id"]?.GetValue<string>());
        Assert.Equal("gpt-5-6-pro", assistant["message"]?["metadata"]?["model_slug"]?.GetValue<string>());
    }

    [Fact]
    public void NestedMappingNode_StillProjectsAuthorAndMetadata()
    {
        var projected = BridgeProjection.Project(CompanionOperation.GetConversationHead, ConversationFixtures.NormalPro());
        Assert.NotNull(projected);
        Assert.True(BridgeProjection.TryValidateProjected(CompanionOperation.GetConversationHead, projected, out var error), error);
        var assistant = projected!["mapping"]?["asst-1"];
        Assert.Equal("assistant", assistant?["message"]?["author"]?["role"]?.GetValue<string>());
        Assert.Equal("req-normal", assistant?["message"]?["metadata"]?["request_id"]?.GetValue<string>());
        Assert.Equal("gpt-5-6-pro", assistant?["message"]?["metadata"]?["model_slug"]?.GetValue<string>());
        Assert.Null(assistant?["parent_id"]);
    }

    [Fact]
    public void ZeroParsedEvents_AreDistinguishableFromEmptyConversation()
    {
        var parser = new ConversationParser(new ModelNormalizer());
        var stripped = parser.Parse(
            ConversationFixtures.MappingWithStrippedAssistantRoles(),
            new ConversationParseContext { ConversationId = "stripped" });
        Assert.False(stripped.SchemaMismatch);
        Assert.Empty(stripped.Events);
        Assert.True(stripped.MappingNodeCount >= 2);
        Assert.Equal(0, stripped.AssistantLikeNodeCount);
        Assert.True(stripped.LoadedWithoutAssistantUsage);
        Assert.True(stripped.NodesMissingRole >= 1);

        var empty = parser.Parse(new JsonObject
        {
            ["conversation_id"] = "empty",
            ["current_node"] = "root",
            ["mapping"] = new JsonObject()
        }, new ConversationParseContext { ConversationId = "empty" });
        Assert.True(empty.SchemaMismatch);
        Assert.Equal(0, empty.MappingNodeCount);
        Assert.False(empty.LoadedWithoutAssistantUsage);
    }
}
