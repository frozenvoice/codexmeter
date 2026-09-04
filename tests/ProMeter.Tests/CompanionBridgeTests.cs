using System.Text.Json.Nodes;
using ProMeter.Companion;
using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class CompanionBridgeTests
{
    [Fact]
    public void MalformedBridgeMessage_NeverBecomesSuccessfulEmptyResult()
    {
        foreach (var raw in new[] { "", "{", "{}", "{\"type\":\"fetchResult\"}", "not-json" })
        {
            var parsed = CompanionBridgeProtocol.Parse(raw);
            var response = CompanionBridgeProtocol.ToProviderResponse(parsed);
            Assert.False(response.IsSuccess);
            Assert.True(response.SchemaMismatch);
            Assert.False(response.IsOffline);
        }
    }

    [Fact]
    public void FetchWithRejectedTarget_IsSchemaMismatch()
    {
        var parsed = CompanionBridgeProtocol.Parse("""{"type":"fetch","path":"//evil.example/x"}""");
        Assert.False(parsed.Accepted);
        var response = CompanionBridgeProtocol.ToProviderResponse(parsed);
        Assert.True(response.SchemaMismatch);
        Assert.False(response.IsSuccess);
    }

    [Fact]
    public void SanitizedConversation_ContainsNoPromptOrResponseText()
    {
        var dirty = new JsonObject
        {
            ["accessToken"] = "synthetic-access-token-do-not-store",
            ["mapping"] = new JsonObject
            {
                ["user-1"] = new JsonObject
                {
                    ["id"] = "user-1",
                    ["parent"] = "root",
                    ["children"] = new JsonArray("asst-1"),
                    ["message"] = new JsonObject
                    {
                        ["id"] = "user-1",
                        ["author"] = new JsonObject { ["role"] = "user" },
                        ["create_time"] = 1_777_500_000,
                        ["content"] = new JsonObject
                        {
                            ["content_type"] = "text",
                            ["parts"] = new JsonArray("SYNTHETIC_PROMPT_TEXT_DO_NOT_STORE"),
                            ["text"] = "SYNTHETIC_ASSISTANT_TEXT_DO_NOT_STORE"
                        },
                        ["metadata"] = new JsonObject
                        {
                            ["request_id"] = "req-synthetic",
                            ["model_slug"] = "gpt-6-pro"
                        }
                    }
                }
            }
        };

        var sanitized = BrowserResponseSanitizer.Sanitize(dirty);
        Assert.NotNull(sanitized);
        Assert.False(BrowserResponseSanitizer.ContainsPromptOrResponseText(sanitized));
        Assert.Null(sanitized["accessToken"]);
        Assert.Equal("req-synthetic", sanitized["mapping"]?["user-1"]?["message"]?["metadata"]?["request_id"]?.GetValue<string>());
        Assert.Equal("gpt-6-pro", sanitized["mapping"]?["user-1"]?["message"]?["metadata"]?["model_slug"]?.GetValue<string>());
    }

    [Fact]
    public void PairingToken_IsStrippedFromHostReplies()
    {
        var raw = """{"type":"helloAck","accepted":true,"pairingToken":"local-pairing-not-chatgpt"}""";
        var stripped = NativeMessagingHost.StripPairingToken(raw);
        Assert.DoesNotContain("local-pairing-not-chatgpt", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("pairingToken", stripped, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AttachPairingToken_PreservesExistingToken()
    {
        var raw = """{"type":"hello","pairingToken":"already-present-token-value"}""";
        Assert.Equal(raw, NativeMessagingHost.AttachPairingToken(raw));
    }
}
