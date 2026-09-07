using ProMeter.Codex;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ChatGptProviderQuotaMetadataTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"model_limits":[]}""")]
    public async Task SuccessfulInitWithoutProLimits_TriesModelsAndRetainsUnknown(string body)
    {
        var calls = new List<(string Method, string Path)>();
        var transport = new PaginationTests.ScriptedTransport((method, path) =>
        {
            calls.Add((method, path));
            if (path == ChatGptEndpoints.ConversationInit)
            {
                return new ProviderResponse { Status = 200, Body = body };
            }

            return new ProviderResponse { Status = 500, Error = "models unavailable" };
        });

        var result = await new ChatGptProvider(transport).TryGetQuotaMetadataAsync();

        Assert.True(result.ProServerStatus.ServerObserved);
        Assert.Equal(ProRestrictionState.Unknown, result.ProServerStatus.RestrictionState);
        Assert.Empty(result.ProServerStatus.ModelLimits);
        Assert.Null(result.ProServerStatus.ResetAt);
        Assert.Equal(ServerResetConfidence.None, result.ProServerStatus.ResetConfidence);
        Assert.False(result.Found);
        Assert.Equal("P?", ProStatusPresentation.From(new QuotaSnapshot { ProServerStatus = result.ProServerStatus }).ProCompactToken);
        Assert.Equal(2, calls.Count);
        Assert.Equal("POST", calls[0].Method);
        Assert.Equal(ChatGptEndpoints.ConversationInit, calls[0].Path);
        Assert.Equal(ChatGptEndpoints.Models, calls[1].Path);
    }

    [Fact]
    public async Task InitFailureAndOrdinaryModelsCatalog_IsNotAProStatusObservation()
    {
        var calls = new List<(string Method, string Path)>();
        var transport = OrdinaryModelsAfterInitFailure(calls);

        var result = await new ChatGptProvider(transport).TryGetQuotaMetadataAsync();

        Assert.False(result.Found);
        Assert.False(result.ProServerStatus.ServerObserved);
        Assert.Equal(ProRestrictionState.Unknown, result.ProServerStatus.RestrictionState);
        Assert.Equal(2, calls.Count);
        Assert.Equal(("POST", ChatGptEndpoints.ConversationInit), calls[0]);
        Assert.Equal(("GET", ChatGptEndpoints.Models), calls[1]);
    }

    [Fact]
    public async Task InitFailureAndOrdinaryModelsCatalog_RetainsPriorRestrictionAsStale()
    {
        var reset = new DateTimeOffset(2026, 9, 6, 5, 20, 13, TimeSpan.Zero);
        var service = new ProServerStatusService(new ProServerStatusStore(TempStatusFile()), new MutableClock(reset.AddHours(-1)));
        service.ApplyFromMetadata(AccountParser.ParseQuotaMetadata(new JsonObject
        {
            ["model_limits"] = new JsonArray
            {
                new JsonObject { ["model_slug"] = "gpt-6-pro", ["resets_after"] = "2026-09-06T05:20:13Z" }
            },
            ["blocked_features"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "reason",
                    ["limit"] = 50,
                    ["resets_after"] = "2026-09-06T05:20:13Z"
                }
            }
        }));
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, service.Current.RestrictionState);
        Assert.Equal(reset, service.Current.ResetAt);
        Assert.False(service.Current.Stale);

        var result = await service.RefreshAsync(new ChatGptProvider(OrdinaryModelsAfterInitFailure()));
        Assert.True(result.TransientFailure);
        Assert.True(result.UsedCache);
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, service.Current.RestrictionState);
        Assert.Equal(reset, service.Current.ResetAt);
        Assert.True(service.Current.Stale);
        var snapshot = new QuotaSnapshot { ProServerStatus = service.Current };
        Assert.Equal("P!" + reset.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "~", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Compact));
        Assert.NotEqual("P?", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Full));
        Assert.NotEqual("P?", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Compact));
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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"model_limits\":[]}")]
    public async Task InitWithoutLimits_UsesModelsResetWhenPresent(string init)
    {
        var transport = new PaginationTests.ScriptedTransport((_, path) => new ProviderResponse
        {
            Status = 200,
            Body = path == ChatGptEndpoints.ConversationInit ? init :
                """{"models":[{"slug":"gpt-6-pro"}],"model_limits":[{"model_slug":"gpt-6-pro","resets_at":"2026-09-13T05:00:00Z"}]}"""
        });
        var result = await new ChatGptProvider(transport).TryGetQuotaMetadataAsync();
        Assert.Equal(DateTimeOffset.Parse("2026-09-13T05:00:00Z"), result.ProServerStatus.ResetAt);
        Assert.Equal(ServerResetConfidence.Server, result.ProServerStatus.ResetConfidence);
    }
    private static PaginationTests.ScriptedTransport OrdinaryModelsAfterInitFailure(
        List<(string Method, string Path)>? calls = null) =>
        new((method, path) =>
        {
            calls?.Add((method, path));
            if (path == ChatGptEndpoints.ConversationInit)
            {
                return new ProviderResponse { Status = 500, Error = "ChatGPT server error." };
            }

            if (path == ChatGptEndpoints.Models)
            {
                return new ProviderResponse
                {
                    Status = 200,
                    Body = """{"models":[{"slug":"gpt-6-pro","title":"GPT-6 Pro"}]}"""
                };
            }

            return new ProviderResponse { Status = 500, Error = "unexpected path" };
        });

    private static string TempStatusFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pro-status.json");
    }
}
