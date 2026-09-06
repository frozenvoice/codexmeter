using ProMeter.Codex;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class SchemaMismatchReasonTests
{
    [Fact]
    public async Task GetJsonAsync_PreservesValidatorReason()
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 0,
            SchemaMismatch = true,
            Error = "conversation collection too large"
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(
            () => new ChatGptProvider(transport).GetConversationIndexAsync(false));
        Assert.True(ex.SchemaMismatch);
        Assert.False(ex.IsOffline);
        Assert.Equal("conversation collection too large", ex.Message);
        Assert.Equal("SchemaMismatch", SyncFailureClassifier.Classify(ex).Category);
    }

    [Fact]
    public async Task GetJsonAsync_DoesNotExposeRawJsonPayload()
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 0,
            SchemaMismatch = true,
            Error = "{\"mapping\":{\"id\":\"secret-node\"}}"
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(
            () => new ChatGptProvider(transport).GetConversationIndexAsync(false));
        Assert.True(ex.SchemaMismatch);
        Assert.Equal(SchemaMismatchReason.GenericMessage, ex.Message);
        Assert.DoesNotContain("secret-node", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyError_FallsBackToGenericSchemaMismatch()
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 0,
            SchemaMismatch = true
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(
            () => new ChatGptProvider(transport).GetConversationIndexAsync(false));
        Assert.Equal(SchemaMismatchReason.GenericMessage, ex.Message);
        Assert.True(ex.SchemaMismatch);
    }

    [Fact]
    public async Task ValidatorReason_ReachesLastErrorAndCoverageBreakdown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "reason.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(
            new ConversationIndexItem { Id = "conv-bc", UpdateTime = now, CreateTime = now - 10 },
            ConversationFixtures.NormalPro("conv-bc", now));
        fixture.LoadOverride = _ => throw new ChatGptProviderException(
            "conversation collection too large",
            0,
            schemaMismatch: true);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";

        var outcome = await engine.SyncAsync(fixture, settings, true);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(
            1,
            engine.LastCoverage.FailureSummary.SchemaMismatchReasons["conversation collection too large"]);
        var record = store.GetConversation("conv-bc");
        Assert.Equal("conversation collection too large", record?.LastError);
        Assert.Equal(ConversationFetchBackoff.SchemaMismatch, record?.LastFetchFailureCategory);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var details = string.Join('\n', DisplayFormatting.CoverageFailureDetailLines(engine.LastCoverage));
            Assert.Contains("응답 형식 불일치    1", details, StringComparison.Ordinal);
            Assert.Contains("대화 노드 수가 안전 검증 한도를 초과함    1", details, StringComparison.Ordinal);
            Assert.DoesNotContain("conv-bc", details, StringComparison.Ordinal);
            Assert.DoesNotContain("6a937057", details, StringComparison.Ordinal);
            Assert.DoesNotContain("{", details, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }

        Assert.NotEqual(AppSyncStatus.UpToDate, outcome.Status);
        var logs = Directory.Exists(Path.Combine(dir, "logs"))
            ? string.Join('\n', Directory.GetFiles(Path.Combine(dir, "logs")).Select(File.ReadAllText))
            : "";
        Assert.Contains("conversation collection too large", logs, StringComparison.Ordinal);
        Assert.Contains("category=SchemaMismatch", logs, StringComparison.Ordinal);
    }

    [Fact]
    public void CoverageDetails_GroupDistinctSafeReasonsWithoutIds()
    {
        var coverage = new CoverageInfo { FailedConversations = 3, ConversationIncomplete = true };
        coverage.FailureSummary.AddThisSync("SchemaMismatch", 0, "conversation collection too large");
        coverage.FailureSummary.AddThisSync("SchemaMismatch", 0, "conversation collection too large");
        coverage.FailureSummary.AddThisSync("SchemaMismatch", 0, "unexpected field: title");

        UiText.SetLanguage(UiLanguage.English);
        try
        {
            var details = string.Join('\n', DisplayFormatting.CoverageFailureDetailLines(coverage));
            Assert.Contains("Response format mismatch    3", details, StringComparison.Ordinal);
            Assert.Contains("  · conversation collection too large    2", details, StringComparison.Ordinal);
            Assert.Contains("  · unknown projected field    1", details, StringComparison.Ordinal);
            Assert.DoesNotContain("unexpected field: title", details, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaMismatch", details, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void Normalize_RejectsJsonAndGenericMismatch()
    {
        Assert.Equal("conversation collection too large", SchemaMismatchReason.Normalize("conversation collection too large"));
        Assert.Equal("unknown projected field", SchemaMismatchReason.Normalize("unexpected field: request_id"));
        Assert.Null(SchemaMismatchReason.Normalize("Provider schema mismatch"));
        Assert.Null(SchemaMismatchReason.Normalize("{\"mapping\":[]}"));
        Assert.Null(SchemaMismatchReason.Normalize(""));
        Assert.Equal(SchemaMismatchReason.GenericMessage, SchemaMismatchReason.ExceptionMessage(null));
        Assert.Equal(
            SchemaMismatchReason.GenericMessage,
            SchemaMismatchReason.ExceptionMessage("{\"accessToken\":\"do-not-store\"}"));
    }

    [Theory]
    [InlineData(2000, true)]
    [InlineData(2001, true)]
    [InlineData(3000, true)]
    [InlineData(8000, true)]
    [InlineData(8001, false)]
    public void ConversationCollectionCap_UsesMaxConversationNodes(int count, bool valid)
    {
        var messages = new JsonArray();
        for (var i = 0; i < count; i++)
        {
            messages.Add(new JsonObject { ["id"] = i.ToString(CultureInfo.InvariantCulture) });
        }

        var body = new JsonObject { ["messages"] = messages };
        var ok = BridgeProjection.TryValidateProjected(CompanionOperation.GetConversationHead, body, out var error);
        Assert.Equal(valid, ok);
        if (valid)
        {
            Assert.True(string.IsNullOrEmpty(error));
        }
        else
        {
            Assert.Equal("conversation collection too large", error);
        }
    }

    [Fact]
    public void MappingCap_UsesSameMaxConversationNodes()
    {
        Assert.Equal(8000, BridgeProjection.MaxConversationNodes);
        Assert.True(BridgeProjection.TryValidateProjected(
            CompanionOperation.GetConversationHead,
            new JsonObject { ["mapping"] = MappingNodes(BridgeProjection.MaxConversationNodes) },
            out var okError), okError);
        Assert.False(BridgeProjection.TryValidateProjected(
            CompanionOperation.GetConversationHead,
            new JsonObject { ["mapping"] = MappingNodes(BridgeProjection.MaxConversationNodes + 1) },
            out var error));
        Assert.Equal("mapping too large", error);
    }

    private static JsonObject MappingNodes(int count)
    {
        var mapping = new JsonObject();
        for (var i = 0; i < count; i++)
        {
            mapping[i.ToString(CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["id"] = i.ToString(CultureInfo.InvariantCulture)
            };
        }

        return mapping;
    }
}
