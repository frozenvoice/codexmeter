using ProMeter.Codex;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ChatGptProviderQuotaMetadataTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"model_limits":[]}""")]
    public async Task SuccessfulInitWithoutProLimits_ReturnsUnknownAndSkipsModels(string body)
    {
        var calls = new List<(string Method, string Path)>();
        var transport = new PaginationTests.ScriptedTransport((method, path) =>
        {
            calls.Add((method, path));
            if (path == ChatGptEndpoints.ConversationInit)
            {
                return new ProviderResponse { Status = 200, Body = body };
            }

            return new ProviderResponse { Status = 500, Error = "models should not be called" };
        });

        var result = await new ChatGptProvider(transport).TryGetQuotaMetadataAsync();

        Assert.True(result.ProServerStatus.ServerObserved);
        Assert.Equal(ProRestrictionState.Unknown, result.ProServerStatus.RestrictionState);
        Assert.Empty(result.ProServerStatus.ModelLimits);
        Assert.Null(result.ProServerStatus.ResetAt);
        Assert.Equal(ServerResetConfidence.None, result.ProServerStatus.ResetConfidence);
        Assert.False(result.Found);
        Assert.Equal("P?", ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = result.ProServerStatus }).ProCompactToken);
        Assert.Single(calls);
        Assert.Equal("POST", calls[0].Method);
        Assert.Equal(ChatGptEndpoints.ConversationInit, calls[0].Path);
        Assert.DoesNotContain(calls, call => call.Path == ChatGptEndpoints.Models);
    }

    [Fact]
    public async Task LiveCorrelatedRestriction_StillReturnsPExclamation()
    {
        var transport = new PaginationTests.ScriptedTransport((_, path) =>
        {
            Assert.Equal(ChatGptEndpoints.ConversationInit, path);
            return new ProviderResponse
            {
                Status = 200,
                Body = """
                    {
                      "model_limits": [
                        { "model_slug": "gpt-5-5-pro", "resets_after": "2026-09-06T05:20:13.110944+00:00" },
                        { "model_slug": "gpt-5-6-pro", "resets_after": "2026-09-06T05:20:13.331007+00:00" },
                        { "model_slug": "gpt-6-pro", "resets_after": "2026-09-06T05:20:13.358703+00:00" }
                      ],
                      "blocked_features": [
                        {
                          "name": "reason",
                          "limit": 50,
                          "resets_after": "2026-09-06T05:20:13.358703+00:00",
                          "block_reason": null
                        }
                      ]
                    }
                    """
            };
        });

        var result = await new ChatGptProvider(transport).TryGetQuotaMetadataAsync();
        Assert.True(result.Found);
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, result.ProServerStatus.RestrictionState);
        Assert.Equal(DateTimeOffset.Parse("2026-09-06T05:20:13.358703+00:00"), result.ProServerStatus.ResetAt);
        Assert.Equal("P!", ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = result.ProServerStatus }).ProCompactToken);
    }

    [Fact]
    public async Task RecognizedProLimitWithoutBlock_ReturnsObservedAvailable()
    {
        var transport = new PaginationTests.ScriptedTransport((_, path) =>
        {
            Assert.Equal(ChatGptEndpoints.ConversationInit, path);
            return new ProviderResponse
            {
                Status = 200,
                Body = """{"model_limits":[{"model_slug":"gpt-6-pro","resets_after":"2026-09-06T05:20:13Z"}]}"""
            };
        });

        var result = await new ChatGptProvider(transport).TryGetQuotaMetadataAsync();
        Assert.True(result.ProServerStatus.ServerObserved);
        Assert.Equal(ProRestrictionState.NoCorrelatedRestrictionObserved, result.ProServerStatus.RestrictionState);
        Assert.Equal("POK", ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = result.ProServerStatus }).ProCompactToken);
        Assert.Equal("P OK", TaskbarStatusFormatter.ChatGptToken(
            new QuotaSnapshot { ProServerStatus = result.ProServerStatus },
            TaskbarStripMode.Full));
    }
}
