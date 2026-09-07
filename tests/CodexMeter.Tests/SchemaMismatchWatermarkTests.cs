using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class SchemaMismatchWatermarkTests
{
    [Fact]
    public async Task SchemaMismatch_DoesNotAdvanceSuccessfulWatermark_OrReportUpToDate()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "mismatch.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-bad", UpdateTime = now, CreateTime = now - 30 };
        fixture.AddConversation(item, new ConversationLoadResult
        {
            Complete = false,
            SchemaMismatch = true,
            Diagnostics = ["mapping incomplete"]
        });
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";

        var first = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.PartialData, first.Status);
        Assert.NotEqual(AppSyncStatus.UpToDate, engine.LastStatus);

        var record = store.GetConversation(item.Id);
        Assert.NotNull(record);
        Assert.Equal(ConversationScanStatus.SchemaMismatch, record!.Status);
        Assert.Null(record.LastSuccessfulScan);
        Assert.Equal(0, record.LastSeenUpdateTime);
        Assert.False(string.IsNullOrWhiteSpace(record.LastError));
        Assert.Equal(1, provider.BodyFetches);

        var second = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.PartialData, second.Status);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
        Assert.Equal(CollectionState.Partial, engine.LastCoverage.OverallState);
        Assert.Null(store.GetConversation(item.Id)?.LastSuccessfulScan);
    }

    [Fact]
    public async Task SuccessfulScanThenMismatch_PreservesPriorWatermark()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "later-mismatch.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-ok", UpdateTime = now, CreateTime = now - 30 };
        fixture.AddConversation(item, ConversationFixtures.NormalPro("conv-ok", now));
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";

        var ok = await engine.SyncAsync(provider, settings, force: true);
        Assert.Equal(AppSyncStatus.UpToDate, ok.Status);
        var success = store.GetConversation(item.Id);
        Assert.Equal(now, success?.LastSeenUpdateTime);
        Assert.NotNull(success?.LastSuccessfulScan);

        item.UpdateTime = now + 90;
        fixture.LoadOverride = _ => new ConversationLoadResult
        {
            Complete = false,
            SchemaMismatch = true,
            Diagnostics = ["provider changed"]
        };

        var failed = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(AppSyncStatus.PartialData, failed.Status);
        Assert.NotEqual(AppSyncStatus.UpToDate, engine.LastStatus);

        var after = store.GetConversation(item.Id);
        Assert.Equal(now, after?.LastSeenUpdateTime);
        Assert.Equal(success!.LastSuccessfulScan, after?.LastSuccessfulScan);
        Assert.Equal(ConversationScanStatus.SchemaMismatch, after?.Status);
        Assert.Equal(2, provider.BodyFetches);

        var retry = await engine.SyncAsync(provider, settings, force: false);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.PartialData, retry.Status);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.Equal(CollectionState.Partial, engine.LastCoverage.OverallState);
    }

    [Fact]
    public async Task PartialConversation_DoesNotBecomeUpToDate()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "partial.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-partial", UpdateTime = now, CreateTime = now - 10 };
        fixture.AddConversation(item, new ConversationLoadResult
        {
            Complete = false,
            SchemaMismatch = false,
            Diagnostics = ["has_previous_page without new cursor"]
        });
        var settings = AppSettings.CreateDefaults();
        var outcome = await engine.SyncAsync(new IncrementalSyncTests.CountingProvider(fixture), settings, true);
        Assert.Equal(AppSyncStatus.PartialData, outcome.Status);
        Assert.True(engine.LastCoverage.ConversationIncomplete);
        Assert.Equal(CoverageConfidence.Incomplete, engine.LastCoverage.Confidence);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
