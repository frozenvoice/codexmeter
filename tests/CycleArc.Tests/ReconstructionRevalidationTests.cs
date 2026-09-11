using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// Old-success revalidation must actually re-fetch bodies. Planning is pure, so counting
/// changed items can never consume the migration budget.
/// </summary>
public class ReconstructionRevalidationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TwentyOldSuccessConversations_AreAllRevalidatedInOneRun()
    {
        var harness = Harness.Create(20);

        var outcome = await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);

        Assert.Equal(20, harness.Provider.BodyFetches);
        Assert.Equal(20, harness.Engine.LastReconstructionRevalidations);
        Assert.NotEqual(0, harness.Provider.BodyFetches);
        Assert.NotEqual(10, harness.Provider.BodyFetches);
        Assert.NotEqual(40, harness.Provider.BodyFetches);
        Assert.Equal(AppSyncStatus.UpToDate, outcome.Status);
        Assert.All(harness.Items, item =>
            Assert.Equal(
                ReconstructionSemantics.Version,
                harness.Store.GetConversation(item.Id)!.ReconstructionVersion));
    }

    [Fact]
    public async Task TwentyFiveOldSuccessConversations_ResumeWithoutStarvation()
    {
        var harness = Harness.Create(25);

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(20, harness.Engine.LastReconstructionRevalidations);
        Assert.Equal(20, harness.Provider.BodyFetches);

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(5, harness.Engine.LastReconstructionRevalidations);
        Assert.Equal(25, harness.Provider.BodyFetches);

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(0, harness.Engine.LastReconstructionRevalidations);
        Assert.Equal(25, harness.Provider.BodyFetches);
    }

    [Fact]
    public void FetchPlanning_IsSideEffectFree()
    {
        var record = new ConversationRecord
        {
            ConversationId = "conv-plan",
            UpdateTime = 100,
            LastSeenUpdateTime = 100,
            LastAttemptedUpdateTime = 100,
            Status = ConversationScanStatus.Ok,
            LastSuccessfulScan = Now.AddDays(-1),
            ReconstructionVersion = ReconstructionSemantics.Version - 1
        };
        var item = new ConversationIndexItem { Id = "conv-plan", UpdateTime = 100, CreateTime = 90 };

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(
                BodyFetchReason.ReconstructionRevalidation,
                ConversationFetchBackoff.DecideBodyFetch(item, record, Now, forceBodyRescan: false));
        }

        // A revalidation candidate is not something the legacy ShouldFetch path claims.
        Assert.False(ConversationFetchBackoff.ShouldFetch(item, record, Now, forceBodyRescan: false));
        Assert.Equal(ReconstructionSemantics.Version - 1, record.ReconstructionVersion);
    }

    [Fact]
    public async Task RepeatedPlanningBeforeSync_DoesNotConsumeBudget()
    {
        var harness = Harness.Create(20);
        for (var i = 0; i < 5; i++)
        {
            foreach (var item in harness.Items)
            {
                ConversationFetchBackoff.DecideBodyFetch(
                    item,
                    harness.Store.GetConversation(item.Id),
                    Now,
                    forceBodyRescan: false);
            }
        }

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(20, harness.Engine.LastReconstructionRevalidations);
        Assert.Equal(20, harness.Provider.BodyFetches);
    }

    [Fact]
    public async Task DuplicateIndexAppearance_ConsumesOneSlotAndOneFetch()
    {
        var harness = Harness.Create(1, alsoInProject: true);

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);

        Assert.Equal(1, harness.Provider.BodyFetches);
        Assert.Equal(1, harness.Engine.LastReconstructionRevalidations);
    }

    [Fact]
    public async Task FailedRevalidation_KeepsOldVersionAndStaysRetryable()
    {
        var harness = Harness.Create(1);
        harness.Fixture.LoadOverride = _ => new ConversationLoadResult
        {
            Complete = false,
            FailureKind = ConversationLoadFailureKind.Timeout,
            Diagnostics = ["legacy-mapping timed out"]
        };

        await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);

        var stored = harness.Store.GetConversation(harness.Items[0].Id)!;
        Assert.Equal(ReconstructionSemantics.Version - 1, stored.ReconstructionVersion);
        Assert.Equal(1, stored.ConsecutiveFetchFailures);
        Assert.Equal(ConversationFetchBackoff.BodyTimeout, stored.LastFetchFailureCategory);
        Assert.Empty(harness.Store.GetUsageEvents());
    }

    [Fact]
    public async Task FortyFiveOldSuccessConversations_MakeProgressUntilDone()
    {
        var harness = Harness.Create(45);
        var fetched = new List<int>();
        var revalidated = new List<int>();

        for (var run = 0; run < 4; run++)
        {
            await harness.Engine.SyncAsync(harness.Provider, harness.Settings, SyncRunOptions.ManualIncremental);
            fetched.Add(harness.Provider.BodyFetches);
            revalidated.Add(harness.Engine.LastReconstructionRevalidations);
        }

        Assert.Equal([20, 20, 5, 0], revalidated);
        Assert.Equal([20, 40, 45, 45], fetched);

        var events = harness.Store.GetUsageEvents();
        Assert.Equal(45, events.Count);
        Assert.Equal(45, events.Select(e => e.DedupeKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(events, e => Assert.Equal(ReconstructionSemantics.Version, e.ReconstructionVersion));
        Assert.All(harness.Items, item =>
            Assert.Equal(
                ReconstructionSemantics.Version,
                harness.Store.GetConversation(item.Id)!.ReconstructionVersion));
    }

    private sealed record Harness(
        SyncEngine Engine,
        SqliteStore Store,
        FixtureChatGptProvider Fixture,
        IncrementalSyncTests.CountingProvider Provider,
        AppSettings Settings,
        List<ConversationIndexItem> Items)
    {
        public static Harness Create(int count, bool alsoInProject = false)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new SqliteStore(Path.Combine(dir, "reval.db"), pooling: false);
            var models = new ModelNormalizer();
            var clock = new MutableClock(Now);
            var engine = new SyncEngine(store, new ConversationParser(models, clock), models, new AppLog(Path.Combine(dir, "logs")), clock);
            var fixture = new FixtureChatGptProvider();
            var items = new List<ConversationIndexItem>();
            var baseTime = Now.AddDays(-1).ToUnixTimeSeconds();
            for (var i = 0; i < count; i++)
            {
                var id = $"conv-old-{i:000}";
                var time = baseTime - i;
                var item = new ConversationIndexItem { Id = id, UpdateTime = time, CreateTime = time - 10 };
                items.Add(item);
                fixture.AddConversation(item, UniquePro(id, time));

                // Successfully scanned by an older reconstruction version, remote unchanged.
                store.ReconcileConversation(new ConversationRecord
                {
                    ConversationId = id,
                    UpdateTime = time,
                    LastSeenUpdateTime = time,
                    LastAttemptedUpdateTime = time,
                    LastScanned = Now.AddDays(-1),
                    LastSuccessfulScan = Now.AddDays(-1),
                    Status = ConversationScanStatus.Ok,
                    ReconstructionVersion = ReconstructionSemantics.Version - 1
                }, []);
            }

            if (alsoInProject)
            {
                fixture.AddProject(
                    new ProjectInfo { Id = "proj-1", Name = "p" },
                    items.Select(item => (
                        new ConversationIndexItem { Id = item.Id, UpdateTime = item.UpdateTime, CreateTime = item.CreateTime },
                        UniquePro(item.Id, item.UpdateTime))));
            }

            var settings = AppSettings.CreateDefaults();
            settings.ResetTimeZoneId = "UTC";
            settings.BodyFetchDelayMilliseconds = 0;
            return new Harness(
                engine,
                store,
                fixture,
                new IncrementalSyncTests.CountingProvider(fixture),
                settings,
                items);
        }
    }

    private static JsonNode UniquePro(string conversationId, double time)
    {
        var json = ConversationFixtures.NormalPro(conversationId, time).ToJsonString()
            .Replace("req-normal", "req-" + conversationId, StringComparison.Ordinal);
        return JsonNode.Parse(json)!;
    }
}
