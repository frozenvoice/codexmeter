using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class ReconcileTests
{
    [Fact]
    public void Reconcile_RemovesStaleSyncEvents_KeepsOfficialExport_ThenUpdatesWatermark()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "reconcile.db"));
        var now = DateTimeOffset.UtcNow;
        var stale = Event("conv-r", "stale-req", UsageSource.ConversationSync, now.AddMinutes(-20));
        var export = Event("conv-r", "export-req", UsageSource.OfficialExport, now.AddMinutes(-10));
        store.UpsertUsageEvents([stale, export]);

        var current = Event("conv-r", "fresh-req", UsageSource.ConversationSync, now);
        store.ReconcileConversation(new ConversationRecord
        {
            ConversationId = "conv-r",
            UpdateTime = 1_777_600_000,
            LastSeenUpdateTime = 1_777_600_000,
            LastScanned = now,
            LastSuccessfulScan = now,
            Status = ConversationScanStatus.Ok,
            Source = "chat"
        }, [current]);

        var events = store.GetUsageEvents();
        Assert.Contains(events, e => e.RequestId == "stale-req");
        Assert.Contains(events, e => e.RequestId == "export-req" && e.Source == UsageSource.OfficialExport);
        Assert.Contains(events, e => e.RequestId == "fresh-req" && e.Source == UsageSource.ConversationSync);

        var record = store.GetConversation("conv-r");
        Assert.Equal(1_777_600_000, record?.LastSeenUpdateTime);
        Assert.Equal(ConversationScanStatus.Ok, record?.Status);
        Assert.NotNull(record?.LastSuccessfulScan);
    }

    [Fact]
    public void Failure_DoesNotOverwriteSuccessfulWatermark()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "fail.db"));
        var scanned = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        store.ReconcileConversation(new ConversationRecord
        {
            ConversationId = "conv-keep",
            UpdateTime = 100,
            LastSeenUpdateTime = 100,
            LastSuccessfulScan = scanned,
            LastScanned = scanned,
            Status = ConversationScanStatus.Ok,
            Source = "chat"
        }, []);

        store.RecordConversationFailure(
            store.GetConversation("conv-keep"),
            new ConversationIndexItem { Id = "conv-keep", UpdateTime = 200, Source = "chat" },
            ConversationScanStatus.SchemaMismatch,
            "schema mismatch");

        var after = store.GetConversation("conv-keep");
        Assert.Equal(100, after?.LastSeenUpdateTime);
        Assert.Equal(scanned, after?.LastSuccessfulScan);
        Assert.Equal(ConversationScanStatus.SchemaMismatch, after?.Status);
        Assert.Equal("schema mismatch", after?.LastError);
    }

    private static UsageEvent Event(string conversationId, string requestId, UsageSource source, DateTimeOffset created) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        RequestId = requestId,
        ConversationId = conversationId,
        MessageId = requestId + "-msg",
        CreatedAt = created,
        NormalizedModel = "GPT-5.6 Sol Pro",
        RawModel = "gpt-5-6-pro",
        Source = source,
        FirstSeenAt = created,
        LastSeenAt = created,
        QuotaFamily = QuotaFamily.GptPro,
        DedupeKey = UsageEvent.BuildDedupeKey(conversationId, requestId, requestId + "-msg")
    };
}
