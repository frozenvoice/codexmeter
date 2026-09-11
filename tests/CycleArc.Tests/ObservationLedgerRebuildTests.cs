using System.Text.Json.Nodes;
using CycleArc.Providers.ChatGpt;
using CycleArc.Services;

namespace CycleArc.Tests;

/// <summary>
/// Canonical requests are derived from the retained observation ledger, not from whichever
/// canonical events the latest response happened to contain.
/// </summary>
public class ObservationLedgerRebuildTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NarrowRescanEnrichment_MergesIntoOneRequestWithExplicitId()
    {
        var harness = Harness.Create("conv-enrich");

        // SCAN 1: graph-linked analysis + visible final, neither carrying a request_id.
        var first = harness.Parse(Conversation(
            Message("U", "user", null, Now.AddMinutes(-6)),
            Message("A", "assistant", "U", Now.AddMinutes(-5), hidden: true, end: false, model: "gpt-5-6-pro"),
            Message("F", "assistant", "A", Now.AddMinutes(-4), model: "gpt-5-6-pro")));
        harness.Reconcile(first);

        var afterFirst = harness.Store.GetUsageEvents();
        Assert.Single(afterFirst);
        Assert.Null(afterFirst[0].RequestId);
        Assert.Equal(DedupeConfidence.Heuristic, afterFirst[0].DedupeConfidence);

        // SCAN 2: narrower response. A now carries request_id R, F is absent from the network reply.
        var second = harness.Parse(Conversation(
            Message("U", "user", null, Now.AddMinutes(-6)),
            Message("A", "assistant", "U", Now.AddMinutes(-5), request: "R", hidden: true, end: false, model: "gpt-5-6-pro")));
        harness.Reconcile(second);

        // The durable ledger still holds F, so the rebuild sees U + A(request_id=R) + F.
        var observations = harness.Store.GetObservations("conv-enrich");
        Assert.Equal(3, observations.Count);
        Assert.Equal("R", observations.Single(o => o.MessageId == "A").RequestId);
        Assert.Contains(observations, o => o.MessageId == "F");

        var events = harness.Store.GetUsageEvents();
        Assert.Single(events);
        Assert.Equal("R", events[0].RequestId);
        Assert.Equal(DedupeConfidence.High, events[0].DedupeConfidence);
        Assert.Equal(UsageEvent.ScopedRequestKey("conv-enrich", "R"), events[0].DedupeKey);
        Assert.Equal(QuotaFamily.GptPro, events[0].QuotaFamily);
    }

    [Fact]
    public void RebuildIsDerivedFromPersistedObservations_NotOnlyTheLatestResponse()
    {
        var harness = Harness.Create("conv-source");
        var full = harness.Parse(Conversation(
            Message("U1", "user", null, Now.AddMinutes(-9)),
            Message("A1", "assistant", "U1", Now.AddMinutes(-8), request: "r1", model: "gpt-5-6-pro"),
            Message("U2", "user", "A1", Now.AddMinutes(-4)),
            Message("A2", "assistant", "U2", Now.AddMinutes(-3), request: "r2", model: "gpt-5-6-pro")));
        harness.Reconcile(full);
        Assert.Equal(2, harness.Store.GetUsageEvents().Count);

        // A branch response that only contains the first turn must not erase the second.
        var narrow = harness.Parse(Conversation(
            Message("U1", "user", null, Now.AddMinutes(-9)),
            Message("A1", "assistant", "U1", Now.AddMinutes(-8), request: "r1", model: "gpt-5-6-pro")));
        harness.Reconcile(narrow);

        var events = harness.Store.GetUsageEvents();
        Assert.Equal(2, events.Count);
        Assert.Equal(["r1", "r2"], RequestIds(events));

        // Replaying the full response again must not increment anything.
        harness.Reconcile(harness.Parse(Conversation(
            Message("U1", "user", null, Now.AddMinutes(-9)),
            Message("A1", "assistant", "U1", Now.AddMinutes(-8), request: "r1", model: "gpt-5-6-pro"),
            Message("U2", "user", "A1", Now.AddMinutes(-4)),
            Message("A2", "assistant", "U2", Now.AddMinutes(-3), request: "r2", model: "gpt-5-6-pro"))));
        Assert.Equal(2, harness.Store.GetUsageEvents().Count);
    }

    [Fact]
    public void ObservationRoundTrip_PreservesEveryFieldCanonicalizationNeeds()
    {
        var harness = Harness.Create("conv-round");
        var parsed = harness.Parse(Conversation(
            Message("U", "user", null, Now.AddMinutes(-3), requested: "gpt-5-6-pro"),
            Message("A", "assistant", "U", Now.AddMinutes(-2), request: "rt", model: "gpt-5-6-pro", hidden: true, end: false),
            Message("F", "assistant", "A", Now.AddMinutes(-1), request: "rt", model: "gpt-5-6-pro")));
        harness.Reconcile(parsed);

        var stored = harness.Store.GetObservations("conv-round").ToDictionary(o => o.MessageId!, StringComparer.Ordinal);
        var analysis = stored["A"];
        Assert.Equal("conv-round", analysis.ConversationId);
        Assert.Equal("U", analysis.ParentMessageId);
        Assert.Equal("rt", analysis.RequestId);
        Assert.Equal("assistant", analysis.Role);
        Assert.True(analysis.Hidden);
        Assert.False(analysis.EndTurn);
        Assert.Equal("all", analysis.Recipient);
        Assert.Equal("gpt-5-6-pro", analysis.ResponseModel);
        Assert.Equal("gpt-5-6-pro", analysis.RawModel);
        Assert.Equal(Now.AddMinutes(-2), analysis.CreatedAt);
        Assert.Equal(UsageSource.ConversationSync, analysis.Source);
        Assert.False(analysis.IsArchived);
        Assert.Equal(ReconstructionSemantics.Version, analysis.ReconstructionVersion);
        Assert.NotEqual(default, analysis.ObservedAt);

        var user = stored["U"];
        Assert.Equal("user", user.Role);
        Assert.Equal("gpt-5-6-pro", user.RequestedModel);

        var final = stored["F"];
        Assert.False(final.Hidden);
        Assert.True(final.EndTurn);

        // The user ancestor is not persisted, but it stays recomputable from the parent graph.
        Assert.Null(final.UserAncestorId);
        var rebuilt = new ReconstructionLedger(new ModelNormalizer(), new MutableClock(Now)).Rebuild(
            harness.Store.GetObservations("conv-round"),
            new ConversationRecord { ConversationId = "conv-round", UpdateTime = 1 });
        Assert.Single(rebuilt);
        Assert.Equal("rt", rebuilt[0].RequestId);
    }

    [Fact]
    public void ExportEvidence_IsPreservedAndReconciledByIdentity()
    {
        var harness = Harness.Create("conv-export");
        var parsed = harness.Parse(Conversation(
            Message("U", "user", null, Now.AddMinutes(-3)),
            Message("A", "assistant", "U", Now.AddMinutes(-2), request: "shared", model: "gpt-5-6-pro")));
        harness.Reconcile(parsed);

        // The same request also arrives through an official export, plus an export-only request.
        harness.Store.UpsertUsageEvents([
            ExportEvent("conv-export", "shared", "A", Now.AddMinutes(-2)),
            ExportEvent("conv-export", "export-only", "X", Now.AddMinutes(-30))
        ]);
        Assert.Equal(2, harness.Store.GetUsageEvents().Count);

        // A later conversation-sync rebuild must not drop the export-only evidence.
        harness.Reconcile(harness.Parse(Conversation(
            Message("U", "user", null, Now.AddMinutes(-3)),
            Message("A", "assistant", "U", Now.AddMinutes(-2), request: "shared", model: "gpt-5-6-pro"))));

        var events = harness.Store.GetUsageEvents();
        Assert.Equal(2, events.Count);
        Assert.Equal(["export-only", "shared"], RequestIds(events));
        Assert.Equal(UsageSource.OfficialExport, events.Single(e => e.RequestId == "export-only").Source);
        Assert.Single(events, e => e.RequestId == "shared");
    }

    [Fact]
    public void ProvenDuplicateMerge_MayReduceTheDerivedCount()
    {
        var harness = Harness.Create("conv-dup");

        // Two sibling analysis fragments observed without ids look like separate turns at first.
        harness.Reconcile(harness.Parse(Conversation(
            Message("U", "user", null, Now.AddMinutes(-9)),
            Message("A", "assistant", "U", Now.AddMinutes(-8), model: "gpt-5-6-pro"))));
        harness.Reconcile(harness.Parse(Conversation(
            Message("U2", "user", null, Now.AddMinutes(-5)),
            Message("B", "assistant", "U2", Now.AddMinutes(-4), model: "gpt-5-6-pro"))));
        Assert.Equal(2, harness.Store.GetUsageEvents().Count);

        // Stronger evidence proves both fragments belong to one generation.
        harness.Reconcile(harness.Parse(Conversation(
            Message("U", "user", null, Now.AddMinutes(-9)),
            Message("A", "assistant", "U", Now.AddMinutes(-8), request: "same", model: "gpt-5-6-pro", hidden: true, end: false),
            Message("B", "assistant", "A", Now.AddMinutes(-4), request: "same", model: "gpt-5-6-pro"))));

        var events = harness.Store.GetUsageEvents();
        Assert.Single(events);
        Assert.Equal("same", events[0].RequestId);
        Assert.Equal("merged_duplicate", events[0].CorrectionReason);

        // Observations are never destroyed by a derived correction.
        Assert.Equal(4, harness.Store.GetObservations("conv-dup").Count);
    }

    private static string[] RequestIds(IEnumerable<UsageEvent> events) =>
        events.Select(e => e.RequestId ?? "").Order(StringComparer.Ordinal).ToArray();

    private static UsageEvent ExportEvent(string conversationId, string requestId, string messageId, DateTimeOffset at) => new()
    {
        Id = "exp-" + requestId,
        RequestId = requestId,
        ConversationId = conversationId,
        MessageId = messageId,
        CreatedAt = at,
        NormalizedModel = "GPT-5.6 Sol Pro",
        RawModel = "gpt-5-6-pro",
        QuotaFamily = QuotaFamily.GptPro,
        Source = UsageSource.OfficialExport,
        FirstSeenAt = at,
        LastSeenAt = at,
        DedupeKey = UsageEvent.ScopedRequestKey(conversationId, requestId),
        DedupeConfidence = DedupeConfidence.High,
        TimestampProvenance = TimestampProvenance.ResponseFragment
    };

    private sealed record Harness(SqliteStore Store, ConversationParser Parser, ReconstructionLedger Ledger, string ConversationId)
    {
        public static Harness Create(string conversationId)
        {
            var dir = Path.Combine(Path.GetTempPath(), "cyclearc-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var store = new SqliteStore(Path.Combine(dir, "ledger.db"), pooling: false);
            var models = new ModelNormalizer();
            var clock = new MutableClock(Now);
            return new Harness(store, new ConversationParser(models, clock), new ReconstructionLedger(models, clock), conversationId);
        }

        public ParseResult Parse(JsonObject data)
        {
            data["conversation_id"] = ConversationId;
            return Parser.Parse(data, new ConversationParseContext { ConversationId = ConversationId });
        }

        public ReconcileOutcome Reconcile(ParseResult parsed) =>
            Store.ReconcileConversation(
                new ConversationRecord
                {
                    ConversationId = ConversationId,
                    UpdateTime = 100,
                    LastSeenUpdateTime = 100,
                    LastSuccessfulScan = Now,
                    Status = ConversationScanStatus.Ok,
                    ReconstructionVersion = ReconstructionSemantics.Version
                },
                parsed.Events,
                parsed.Observations,
                Ledger);
    }

    private static JsonObject Message(
        string id,
        string role,
        string? parent,
        DateTimeOffset? time,
        string? request = null,
        string? model = null,
        bool hidden = false,
        bool end = true,
        string? requested = null)
    {
        var metadata = new JsonObject();
        if (request is not null) metadata["request_id"] = request;
        if (model is not null) metadata["model_slug"] = model;
        if (requested is not null) metadata["requested_model"] = requested;
        metadata["is_visually_hidden_from_conversation"] = hidden;
        return new JsonObject
        {
            ["id"] = id,
            ["parent"] = parent,
            ["children"] = new JsonArray(),
            ["message"] = new JsonObject
            {
                ["id"] = id,
                ["author"] = new JsonObject { ["role"] = role },
                ["create_time"] = time?.ToUnixTimeSeconds(),
                ["end_turn"] = end,
                ["recipient"] = "all",
                ["metadata"] = metadata
            }
        };
    }

    private static JsonObject Conversation(params JsonObject[] nodes)
    {
        var mapping = new JsonObject();
        foreach (var node in nodes)
        {
            mapping[node["id"]!.GetValue<string>()] = node;
        }

        return new JsonObject
        {
            ["conversation_id"] = "ledger",
            ["update_time"] = Now.ToUnixTimeSeconds(),
            ["create_time"] = Now.AddDays(-1).ToUnixTimeSeconds(),
            ["current_node"] = nodes[^1]["id"]!.GetValue<string>(),
            ["mapping"] = mapping
        };
    }
}
