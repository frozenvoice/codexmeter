using CycleArc.Providers.ChatGpt;

namespace CycleArc.Tests;

public class QuotaProjectionTests
{
    [Fact]
    public void NativeProjectionKeepsSafeResetAndBlockedFields()
    {
        var raw = LiveQuota();
        var projected = BridgeProjection.Project(CompanionOperation.GetQuotaInit, raw)!;
        Assert.True(BridgeProjection.TryValidateProjected(CompanionOperation.GetQuotaInit, projected, out var error), error);
        Assert.Equal("gpt-6-pro", projected["model_limits"]?[0]?["model_slug"]?.GetValue<string>());
        Assert.Equal("2026-09-06T05:20:13.358703+00:00", projected["model_limits"]?[0]?["resets_after"]?.GetValue<string>());
        Assert.Equal("reason", projected["blocked_features"]?[0]?["name"]?.GetValue<string>());
        Assert.Equal(50, projected["blocked_features"]?[0]?["limit"]?.GetValue<int>());
        var blocked = Assert.IsType<JsonObject>(projected["blocked_features"]?[0]);
        Assert.True(blocked.TryGetPropertyValue("block_reason", out var reason));
        Assert.True(reason is null || (reason is JsonValue value && value.GetValueKind() == JsonValueKind.Null));
        Assert.Null(projected["account_id"]);
        Assert.Null(projected["model_limits"]?[0]?["prompt"]);
        Assert.Null(projected["blocked_features"]?[0]?["conversation_id"]);
    }

    [Fact]
    public void NativeAllowlistRejectsLeakyKeys()
    {
        var leaky = new JsonObject
        {
            ["model_limits"] = new JsonArray
            {
                new JsonObject
                {
                    ["model_slug"] = "gpt-6-pro",
                    ["resets_after"] = "2026-09-06T05:20:13Z",
                    ["email"] = "user@example.invalid"
                }
            }
        };
        Assert.False(BridgeProjection.TryValidateProjected(CompanionOperation.GetQuotaInit, leaky, out var error));
        Assert.Contains("email", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAllowlistRejectsNestedBlockedPayload()
    {
        var leaky = new JsonObject
        {
            ["blocked_features"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "reason",
                    ["description"] = new JsonObject { ["text"] = "SYNTHETIC_PROMPT_DO_NOT_STORE" }
                }
            }
        };
        Assert.False(BridgeProjection.TryValidateProjected(CompanionOperation.GetQuotaInit, leaky, out var error));
        Assert.Contains("description", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ProjectedLiveShapeParsesWithoutAuthoritativeCount()
    {
        var projected = BridgeProjection.Project(CompanionOperation.GetQuotaInit, LiveQuota())!;
        var parsed = AccountParser.ParseQuotaMetadata(projected);
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, parsed.ProServerStatus.RestrictionState);
        Assert.False(parsed.WeeklyWindow(SubscriptionPreset.Pro100)?.IsAuthoritative ?? false);
    }

    private static JsonObject LiveQuota() => new()
    {
        ["account_id"] = "acc-secret",
        ["model_limits"] = new JsonArray
        {
            new JsonObject
            {
                ["model_slug"] = "gpt-6-pro",
                ["resets_after"] = "2026-09-06T05:20:13.358703+00:00",
                ["prompt"] = "SYNTHETIC_PROMPT_DO_NOT_STORE"
            }
        },
        ["blocked_features"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "reason",
                ["limit"] = 50,
                ["resets_after"] = "2026-09-06T05:20:13.358703+00:00",
                ["block_reason"] = JsonNode.Parse("null"),
                ["description"] = "server supplied restriction notice",
                ["conversation_id"] = "c-secret"
            }
        }
    };
}
