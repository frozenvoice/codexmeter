using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ImportDedupeTests
{
    [Fact]
    public void DuplicateImport_DoesNotIncreaseUsage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "import.db"));
        var models = new ModelNormalizer();
        var importer = new ConversationExportImporter(new ConversationParser(models), models);
        var json = ConversationFixtures.OfficialExportJson();
        var first = importer.Import(json, includeHistorical: true, DateTimeOffset.UnixEpoch);
        store.UpsertUsageEvents(first.Events);
        var afterFirst = store.GetUsageEvents().Count;
        var second = importer.Import(json, includeHistorical: true, DateTimeOffset.UnixEpoch);
        store.UpsertUsageEvents(second.Events);
        Assert.Equal(afterFirst, store.GetUsageEvents().Count);
        Assert.True(afterFirst >= 4);
    }
}
