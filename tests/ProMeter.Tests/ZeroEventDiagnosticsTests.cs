using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ZeroEventDiagnosticsTests
{
    [Fact]
    public async Task MultipleLoadedConversationsWithZeroEvents_AreSchemaSuspect()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "zero.db"));
        var models = new ModelNormalizer();
        var logDir = Path.Combine(dir, "logs");
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(logDir));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(
            new ConversationIndexItem { Id = "conv-a", UpdateTime = now, CreateTime = now - 10 },
            ConversationFixtures.MappingWithStrippedAssistantRoles("conv-a", now));
        fixture.AddConversation(
            new ConversationIndexItem { Id = "conv-b", UpdateTime = now - 1, CreateTime = now - 20 },
            ConversationFixtures.MappingWithStrippedAssistantRoles("conv-b", now - 1));
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";

        var outcome = await engine.SyncAsync(fixture, settings, force: true);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, outcome.Status);
        Assert.Equal(SyncEngine.MissingAssistantUsageDiagnostic, engine.LastStatusDetail);
        Assert.True(engine.LastCoverage.HistoryLoadedWithoutUsage);
        Assert.Equal(2, engine.LastCoverage.LoadedConversations);
        Assert.Equal(0, engine.LastCoverage.ConversationsWithEvents);
        Assert.Equal(2, engine.LastCoverage.ZeroEventConversations);
        Assert.True(engine.LastCoverage.FailedConversations >= 2);
        Assert.Null(store.GetConversation("conv-a")?.LastSuccessfulScan);
        Assert.Null(store.GetConversation("conv-b")?.LastSuccessfulScan);
        Assert.Equal(ConversationScanStatus.SchemaMismatch, store.GetConversation("conv-a")?.Status);

        var snapshot = new QuotaEngine().Build(
            store.GetUsageEvents(),
            settings,
            DateTimeOffset.UtcNow,
            engine.LastSyncCompleted,
            engine.LastCoverage,
            engine.LastQuotaMetadata,
            engine.LastStatus,
            engine.LastStatusDetail);
        Assert.True(snapshot.DisplayUsageUnavailable);
        Assert.Equal("?", DisplayFormatting.UsageLabel(snapshot));
        Assert.Equal("Incomplete reconstruction", DisplayFormatting.CountSourceLabel(snapshot));
        Assert.DoesNotContain("0 / 50", DisplayFormatting.UsageLabel(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("0 / 50", DisplayFormatting.Headline(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("0 / 50", DisplayFormatting.Tooltip(snapshot), StringComparison.Ordinal);

        var logs = Directory.GetFiles(logDir, "*.log").SelectMany(File.ReadAllLines).ToList();
        Assert.Contains(logs, line => line.Contains("sync diagnostics", StringComparison.Ordinal));
        Assert.Contains(logs, line => line.Contains("zeroEvents=2", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains("SYNTHETIC_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenuineCompleteScanWithZeroProUsage_StillDisplaysZero()
    {
        var dir = NewTempDir();
        using var store = new SqliteStore(Path.Combine(dir, "reason.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(
            new ConversationIndexItem { Id = "conv-r1", UpdateTime = now, CreateTime = now - 10 },
            ConversationFixtures.ReasoningMessage("conv-r1", now));
        fixture.AddConversation(
            new ConversationIndexItem { Id = "conv-r2", UpdateTime = now - 1, CreateTime = now - 20 },
            ConversationFixtures.ReasoningMessage("conv-r2", now - 1));
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";

        var outcome = await engine.SyncAsync(fixture, settings, force: true);
        Assert.Equal(AppSyncStatus.UpToDate, outcome.Status);
        Assert.False(engine.LastCoverage.HistoryLoadedWithoutUsage);
        Assert.Equal(2, engine.LastCoverage.ConversationsWithEvents);
        Assert.Equal(0, engine.LastCoverage.ZeroEventConversations);

        var snapshot = new QuotaEngine().Build(
            store.GetUsageEvents(),
            settings,
            DateTimeOffset.UtcNow,
            engine.LastSyncCompleted,
            engine.LastCoverage,
            new QuotaMetadataSet
            {
                ProServerStatus = new ProServerStatus { LastConfirmedResetAt = DateTimeOffset.UtcNow.AddDays(-1) }
            },
            engine.LastStatus,
            engine.LastStatusDetail);
        Assert.False(snapshot.DisplayUsageUnavailable);
        Assert.Equal(0, snapshot.Used);
        Assert.Equal("0+", DisplayFormatting.UsageLabel(snapshot));
        Assert.DoesNotContain("0+", DisplayFormatting.FlyoutHeader(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("0 / 50", DisplayFormatting.Headline(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain("0 / 50", DisplayFormatting.UsageLabel(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteZeroReconstruction_HidesFactualZero()
    {
        var coverage = new CoverageInfo
        {
            NormalChats = true,
            ConversationIncomplete = true,
            HistoryLoadedWithoutUsage = true,
            LoadedConversations = 8,
            ZeroEventConversations = 8
        };
        var snapshot = new QuotaEngine().Build(
            [],
            AppSettings.CreateDefaults(),
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
            DateTimeOffset.UtcNow,
            coverage,
            new QuotaMetadataSet(),
            AppSyncStatus.ProviderSchemaMismatch);
        Assert.True(snapshot.DisplayUsageUnavailable);
        Assert.Equal("?", DisplayFormatting.UsageLabel(snapshot));
        Assert.Equal("Incomplete reconstruction", DisplayFormatting.CountSourceLabel(snapshot));
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
