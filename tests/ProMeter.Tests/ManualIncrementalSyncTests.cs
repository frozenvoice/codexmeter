using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ManualIncrementalSyncTests
{
    [Fact]
    public async Task UnchangedConversations_AreNotBodyFetchedOnManualRefresh()
    {
        var (engine, provider, settings, items) = CreateHarness(75);
        await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(75, provider.BodyFetches);

        await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(75, provider.BodyFetches);

        items[0].UpdateTime += 30;
        await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(76, provider.BodyFetches);

        await engine.SyncAsync(
            provider,
            settings,
            new SyncRunOptions { Origin = SyncOrigin.Manual, BypassPause = true, ForceBodyRescan = true });
        Assert.Equal(151, provider.BodyFetches);
    }

    [Fact]
    public void NormalUiRefreshPaths_AreIncremental()
    {
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("SyncRunOptions.Manual(bypassPause)", app, StringComparison.Ordinal);
        Assert.Contains("SyncRunOptions.ManualIncremental", app, StringComparison.Ordinal);
        Assert.Contains("SyncRunOptions.StartupIncremental", app, StringComparison.Ordinal);
        Assert.Contains("SyncRunOptions.Auto", app, StringComparison.Ordinal);
        Assert.Contains("SyncRunOptions.FlyoutStale", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceBodyRescan = true", app, StringComparison.Ordinal);
        Assert.Contains("RefreshAllAsync(true", app, StringComparison.Ordinal);
        Assert.Contains("_taskbarStrip.RefreshRequested", app, StringComparison.Ordinal);
        Assert.Contains("_widget.RefreshRequested", app, StringComparison.Ordinal);
        Assert.Contains("_flyout.SyncRequested", app, StringComparison.Ordinal);
        Assert.Contains("_tray.SyncRequested", app, StringComparison.Ordinal);
    }

    private static (SyncEngine Engine, IncrementalSyncTests.CountingProvider Provider, AppSettings Settings, List<ConversationIndexItem> Items)
        CreateHarness(int count)
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteStore(Path.Combine(dir, "manual.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var items = new List<ConversationIndexItem>();
        for (var i = 0; i < count; i++)
        {
            var id = $"conv-{i:000}";
            var item = new ConversationIndexItem { Id = id, UpdateTime = now - i, CreateTime = now - i - 10 };
            items.Add(item);
            fixture.AddConversation(item, ConversationFixtures.NormalPro(id, now - i));
        }

        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;
        return (engine, new IncrementalSyncTests.CountingProvider(fixture), settings, items);
    }

    private static string Find(string relative)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(relative);
    }
}
