using System.Text.Json.Nodes;
using CodexMeter.Codex;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class MeteringAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DistinctRequestIds_AreNotMerged()
    {
        var parsed = Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-4)),
            Message("a", "assistant", "user", Now.AddMinutes(-3), "req-a", "gpt-5-6-pro"),
            Message("b", "assistant", "user", Now.AddMinutes(-1), "req-b", "gpt-5-6-pro")));
        Assert.Equal(2, parsed.Events.Count);
    }

    [Fact]
    public void SiblingRegenerations_AreSeparate()
    {
        var parsed = Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-4)),
            Message("first", "assistant", "user", Now.AddMinutes(-3), "req-1", "gpt-5-6-pro"),
            Message("second", "assistant", "user", Now.AddMinutes(-1), "req-2", "gpt-5-6-pro")));
        Assert.Equal(2, parsed.Events.Count);
    }

    [Fact]
    public void StreamingPartialThenFinalThenRefetch_IsOneCanonicalRequest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "stream.db"), pooling: false);
        var record = new ConversationRecord { ConversationId = "conv-stream", UpdateTime = 1, Status = ConversationScanStatus.Ok };
        var parser = new ConversationParser(new ModelNormalizer(), new MutableClock(Now));
        var partial = parser.Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-2)),
            Message("analysis", "assistant", "user", Now.AddMinutes(-1), "req-stream", "gpt-5-6-pro", true, false)), recordContext("conv-stream"));
        store.ReconcileConversation(record, partial.Events, partial.Observations);
        var final = parser.Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-2)),
            Message("analysis", "assistant", "user", Now.AddMinutes(-1), "req-stream", "gpt-5-6-pro", true, false),
            Message("final", "assistant", "analysis", Now, "req-stream", "gpt-5-6-pro")), recordContext("conv-stream"));
        store.ReconcileConversation(record, final.Events, final.Observations);
        store.ReconcileConversation(record, final.Events, final.Observations);
        Assert.Single(store.GetUsageEvents());
    }

    [Fact]
    public void PageOrderPermutations_AreEquivalent()
    {
        var first = Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-3)),
            Message("final", "assistant", "user", Now.AddMinutes(-1), "req-order", "gpt-5-6-pro"),
            Message("analysis", "assistant", "user", Now.AddMinutes(-2), "req-order", "gpt-5-6-pro", true, false)));
        var second = Parse(Conversation(
            Message("analysis", "assistant", "user", Now.AddMinutes(-2), "req-order", "gpt-5-6-pro", true, false),
            Message("user", "user", null, Now.AddMinutes(-3)),
            Message("final", "assistant", "user", Now.AddMinutes(-1), "req-order", "gpt-5-6-pro")));
        Assert.Single(first.Events);
        Assert.Single(second.Events);
        Assert.Equal(first.Events[0].RequestId, second.Events[0].RequestId);
        Assert.Equal(first.Events[0].QuotaFamily, second.Events[0].QuotaFamily);
    }

    [Fact]
    public void FullThenNarrowThenFull_DoesNotDuplicateOrLose()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "ab.db"), pooling: false);
        var record = new ConversationRecord { ConversationId = "conv-ab", UpdateTime = 1, Status = ConversationScanStatus.Ok };
        UsageEvent Make(string id) => new()
        {
            Id = id, RequestId = id, ConversationId = "conv-ab", MessageId = id, CreatedAt = Now,
            DedupeKey = UsageEvent.ScopedRequestKey("conv-ab", id), QuotaFamily = QuotaFamily.GptPro,
            RawModel = "gpt-5-6-pro", FirstSeenAt = Now, LastSeenAt = Now, Source = UsageSource.ConversationSync
        };
        store.ReconcileConversation(record, [Make("A"), Make("B")]);
        store.ReconcileConversation(record, [Make("A")]);
        store.ReconcileConversation(record, [Make("A"), Make("B")]);
        Assert.Equal(2, store.GetUsageEvents().Count);
    }

    [Fact]
    public void HeuristicThenRequestId_Merges()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "alias.db"), pooling: false);
        var record = new ConversationRecord { ConversationId = "conv-alias", UpdateTime = 1, Status = ConversationScanStatus.Ok };
        var heuristic = new UsageEvent
        {
            Id = "h", ConversationId = "conv-alias", MessageId = "final", CreatedAt = Now,
            DedupeKey = "turn:conv-alias:user", QuotaFamily = QuotaFamily.GptPro, RawModel = "gpt-5-6-pro",
            FirstSeenAt = Now, LastSeenAt = Now, DedupeConfidence = DedupeConfidence.Heuristic, IdentityAliases = "final"
        };
        var explicitId = new UsageEvent
        {
            Id = "e", ConversationId = "conv-alias", MessageId = "final", RequestId = "req-later", CreatedAt = Now,
            DedupeKey = UsageEvent.ScopedRequestKey("conv-alias", "req-later"), QuotaFamily = QuotaFamily.GptPro,
            RawModel = "gpt-5-6-pro", FirstSeenAt = Now, LastSeenAt = Now, DedupeConfidence = DedupeConfidence.High,
            IdentityAliases = "final"
        };
        store.ReconcileConversation(record, [heuristic]);
        store.ReconcileConversation(record, [explicitId]);
        Assert.Single(store.GetUsageEvents());
        Assert.Equal("req-later", store.GetUsageEvents()[0].RequestId);
    }

    [Fact]
    public void RequestedProUnknownResponse_IsNotCountedAsPro()
    {
        var parsed = Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-2), requested: "gpt-5-6-pro"),
            Message("final", "assistant", "user", Now.AddMinutes(-1), "req-unknown")));
        Assert.Single(parsed.Events);
        Assert.NotEqual(QuotaFamily.GptPro, parsed.Events[0].QuotaFamily);
        Assert.Equal(ModelEvidenceConfidence.RequestedOnly, parsed.Events[0].ModelConfidence);
    }

    [Fact]
    public void ConflictingServedModels_AreUnresolvedNotPro()
    {
        var parsed = Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-2)),
            Message("analysis", "assistant", "user", Now.AddMinutes(-1), "req-conf", "gpt-5-6-pro", true, false),
            Message("final", "assistant", "analysis", Now, "req-conf", "gpt-5-6")));
        Assert.Single(parsed.Events);
        Assert.Equal(QuotaFamily.Unknown, parsed.Events[0].QuotaFamily);
        Assert.False(parsed.Events[0].Countable);
        Assert.Equal(UnresolvedEvidenceKind.Model, parsed.Events[0].UnresolvedKind);
    }

    [Fact]
    public void CatalogHydrate_SurvivesRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "models.db");
        using (var store = new SqliteStore(path, pooling: false))
        {
            store.ObserveModel("synthetic-catalog-pro-model", "GPT-6 Pro", "catalog");
        }

        using var reopened = new SqliteStore(path, pooling: false);
        var models = new ModelNormalizer();
        models.Hydrate(reopened.GetObservedModels());
        Assert.Equal(QuotaFamily.GptPro, models.Resolve(null, "synthetic-catalog-pro-model").Family);
    }

    [Fact]
    public void RegenerationDoesNotInheritOldUserTimeWhenResponseTimeMissing()
    {
        var parsed = Parse(Conversation(
            Message("user", "user", null, Now.AddDays(-10)),
            Message("first", "assistant", "user", Now.AddDays(-10), "req-old", "gpt-5-6-pro"),
            Message("second", "assistant", "user", null, "req-new", "gpt-5-6-pro")));
        var newer = parsed.Events.Single(e => e.RequestId == "req-new");
        Assert.Equal(TimestampProvenance.Unknown, newer.TimestampProvenance);
        Assert.False(newer.Countable);
    }

    [Fact]
    public void ResetSpanningRequest_IsPeriodAmbiguous()
    {
        var reset = Now;
        var usage = new UsageEvent
        {
            ConversationId = "conv", RequestId = "span", MessageId = "m", CreatedAt = Now.AddMinutes(1),
            RequestStartedAt = Now.AddMinutes(-5), ResponseCompletedAt = Now.AddMinutes(5),
            QuotaFamily = QuotaFamily.GptPro, Countable = true, TimestampProvenance = TimestampProvenance.ResponseFragment,
            DedupeKey = "req:conv:span"
        };
        var settings = new AppSettings { ResetAnchorConfigured = true, ResetTimeZoneId = "UTC" };
        var snapshot = new QuotaEngine().Build(
            [usage],
            settings,
            Now.AddMinutes(10),
            Now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet { ProServerStatus = new ProServerStatus { LastConfirmedResetAt = reset } },
            AppSyncStatus.UpToDate);
        Assert.Equal(0, snapshot.ReconstructedUsed);
        Assert.True(snapshot.UnresolvedCount >= 1);
    }

    [Fact]
    public void UnrelatedDailyReset_DoesNotReplaceWeekly()
    {
        var weekly = Now.AddDays(3);
        var status = new ProServerStatus
        {
            ServerObserved = true,
            ResetConfidence = ServerResetConfidence.Server,
            ResetAt = Now.AddHours(3),
            ModelLimits = [new ProModelLimit { Slug = "gpt-5-6-pro", ResetAt = Now.AddHours(3), Description = "daily" }]
        };
        var settings = new AppSettings();
        settings.ApplyPreset(SubscriptionPreset.Pro200);
        var period = ProQuotaPeriodResolver.Resolve(settings, Now, status, weekly);
        Assert.Equal(weekly, period.End);
    }

    [Fact]
    public void LocalSolWeek_IsNotProPeriod()
    {
        var settings = new AppSettings
        {
            ResetTimeZoneId = "UTC",
            ResetWeekday = DayOfWeek.Monday,
            ResetAnchorConfigured = true
        };
        var reset = new DateTimeOffset(2026, 9, 6, 14, 20, 0, TimeSpan.Zero);
        var todayMorning = new DateTimeOffset(2026, 9, 6, 2, 0, 0, TimeSpan.Zero);
        var afterReset = new DateTimeOffset(2026, 9, 6, 15, 0, 0, TimeSpan.Zero);
        UsageEvent Sol(string id, DateTimeOffset at) => new()
        {
            Id = id, ConversationId = "c", MessageId = id, CreatedAt = at, QuotaFamily = QuotaFamily.SolReasoning,
            DedupeKey = id, TimestampProvenance = TimestampProvenance.ResponseFragment, Countable = true
        };
        var snapshot = new QuotaEngine().Build(
            [Sol("a", todayMorning), Sol("b", afterReset), Sol("c", afterReset)],
            settings,
            afterReset,
            afterReset,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet { ProServerStatus = new ProServerStatus { LastConfirmedResetAt = reset, ResetAt = reset, ResetConfidence = ServerResetConfidence.Server, ServerObserved = true } },
            AppSyncStatus.UpToDate);
        Assert.Equal(3, snapshot.Reasoning.ThisWeek);
        Assert.Equal(3, snapshot.Reasoning.Today);
        Assert.Equal(0, snapshot.ReconstructedUsed);
    }

    [Fact]
    public void DailyChart_UsesConfiguredTimezoneNotUtcDate()
    {
        var settings = new AppSettings { ResetTimeZoneId = "Korea Standard Time" };
        var justAfterKstMidnight = new DateTimeOffset(2026, 9, 6, 15, 30, 0, TimeSpan.Zero);
        var events = new[]
        {
            new UsageEvent
            {
                Id = "k", ConversationId = "c", MessageId = "k", CreatedAt = justAfterKstMidnight,
                QuotaFamily = QuotaFamily.GptPro, DedupeKey = "k", TimestampProvenance = TimestampProvenance.ResponseFragment,
                Countable = true
            }
        };
        var trend = new QuotaEngine().BuildTrend(events, justAfterKstMidnight.AddHours(-2), justAfterKstMidnight.AddHours(2), settings);
        Assert.Contains(trend, point => point.Date == new DateOnly(2026, 9, 7) && point.ProCount == 1);
    }

    [Fact]
    public void Presentation_DoesNotDeriveExactRemainingFromReconstruction()
    {
        var snapshot = new QuotaSnapshot
        {
            Used = 7,
            Limit = 50,
            ReconstructedUsed = 7,
            CurrentCycleKnown = true,
            Coverage = new CoverageInfo { CountConfidence = CoverageConfidence.Estimated }
        };
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.False(presentation.ExactRemainingAvailable);
        Assert.Equal(UiText.ExactRemainingUnavailable, presentation.ExactRemainingText);
        Assert.DoesNotContain("7 / 50", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
        Assert.DoesNotContain("43", presentation.ExactRemainingText, StringComparison.Ordinal);
        Assert.Equal(UiText.ReconstructedCount(7), presentation.ConfirmedRequestsText);
        Assert.Equal("P? 7~", TaskbarStatusFormatter.ChatGptToken(snapshot, TaskbarStripMode.Full));
    }

    [Fact]
    public void SharedMessageIds_AcrossConversations_RemainSeparateRequests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "ids.db"), pooling: false);
        var models = new ModelNormalizer();
        var parser = new ConversationParser(models);
        foreach (var id in new[] { "conv-a", "conv-b" })
        {
            var parsed = parser.Parse(
                ConversationFixtures.NormalPro(id, 1_777_500_000),
                new ConversationParseContext { ConversationId = id });
            store.ReconcileConversation(
                new ConversationRecord { ConversationId = id, UpdateTime = 1_777_500_000, Status = ConversationScanStatus.Ok },
                parsed.Events,
                parsed.Observations);
        }

        var events = store.GetUsageEvents();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.ConversationId == "conv-a");
        Assert.Contains(events, e => e.ConversationId == "conv-b");
        Assert.True(store.GetObservations("conv-a").Count >= 1);
        Assert.True(store.GetObservations("conv-b").Count >= 1);
    }

    [Fact]
    public void Presentation_UnresolvedRow_OmitsZero()
    {
        var snapshot = new QuotaSnapshot { ReconstructedUsed = 3, UnresolvedCount = 2, CurrentCycleKnown = true };
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.Equal(2, presentation.UnresolvedCount);
        Assert.Equal(UiText.UnresolvedPendingCount(2), UiText.UnresolvedPendingCount(presentation.UnresolvedCount));
    }

    [Fact]
    public void MigrationTwice_IsIdempotent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "mig.db");
        using (var first = new SqliteStore(path, pooling: false))
        {
            first.ReconcileConversation(new ConversationRecord { ConversationId = "c", UpdateTime = 1 }, [
                new UsageEvent
                {
                    Id = "a", RequestId = "a", ConversationId = "c", MessageId = "a", CreatedAt = Now,
                    DedupeKey = UsageEvent.ScopedRequestKey("c", "a"), QuotaFamily = QuotaFamily.GptPro,
                    FirstSeenAt = Now, LastSeenAt = Now
                }
            ]);
        }

        using var second = new SqliteStore(path, pooling: false);
        using var third = new SqliteStore(path, pooling: false);
        Assert.Single(third.GetUsageEvents());
        Assert.Equal(ConversationFetchBackoff.ReconstructionSemanticsVersion.ToString(CultureInfo.InvariantCulture), third.GetState(SqliteStore.ReconstructionSchemaStateKey));
    }

    [Fact]
    public void BackupApi_CopiesWalAwareDatabase()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var live = Path.Combine(dir, "live.db");
        var backup = Path.Combine(dir, "backup.db");
        using (var store = new SqliteStore(live, pooling: false))
        {
            store.SetState("probe", "ok");
            store.BackupTo(backup);
        }

        using var copied = new SqliteStore(backup, pooling: false);
        Assert.Equal("ok", copied.GetState("probe"));
    }

    [Fact]
    public void MeteringSnapshot_HasNoContentFields()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "snap.db"), pooling: false);
        store.RecordMeteringSnapshot("sync-complete", new { reconstructionVersion = 4, displayedReconstruction = 3, unresolved = 1 });
        Assert.Contains("sync-complete", store.GetMeteringSnapshotKinds());
    }

    [Fact]
    public void LoaderParserStoreQuotaPresentation_Integration()
    {
        var conversation = Conversation(
            Message("user", "user", null, Now.AddMinutes(-2)),
            Message("analysis", "assistant", "user", Now.AddMinutes(-1), "req-int", "gpt-5-6-pro", true, false),
            Message("final", "assistant", "analysis", Now, model: "gpt-5-6-pro"));
        var load = ConversationDetailLoader.FromFixture(conversation);
        Assert.True(load.Complete);
        var parsed = new ConversationParser(new ModelNormalizer(), new MutableClock(Now)).Parse(
            load.Conversation, recordContext("synthetic-audit"));
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "int.db"), pooling: false);
        store.ReconcileConversation(new ConversationRecord { ConversationId = "synthetic-audit", UpdateTime = Now.ToUnixTimeSeconds(), Status = ConversationScanStatus.Ok }, parsed.Events, parsed.Observations);
        var events = store.GetUsageEvents();
        var snapshot = new QuotaEngine().Build(events, new AppSettings { ResetAnchorConfigured = true, ResetTimeZoneId = "UTC" }, Now.AddMinutes(1), Now, new CoverageInfo { NormalChats = true, ConversationIncomplete = true }, null, AppSyncStatus.PartialData);
        var presentation = ProStatusPresentation.From(snapshot);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        Assert.False(presentation.ExactRemainingAvailable);
        Assert.Contains("1", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
        Assert.DoesNotContain(" / 50", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
        Assert.Equal(UiText.ExactRemainingUnavailable, presentation.ExactRemainingText);
    }

    private static ConversationParseContext recordContext(string id) => new() { ConversationId = id };

    private static ParseResult Parse(JsonObject data) =>
        new ConversationParser(new ModelNormalizer(), new MutableClock(Now)).Parse(data, new ConversationParseContext { ConversationId = "synthetic-audit" });

    private static JsonObject Message(string id, string role, string? parent, DateTimeOffset? time,
        string? request = null, string? model = null, bool hidden = false, bool end = true, string? requested = null)
    {
        var metadata = new JsonObject();
        if (request is not null) metadata["request_id"] = request;
        if (model is not null) metadata["model_slug"] = model;
        if (requested is not null) metadata["requested_model"] = requested;
        metadata["is_visually_hidden_from_conversation"] = hidden;
        return new JsonObject
        {
            ["id"] = id, ["parent"] = parent, ["children"] = new JsonArray(),
            ["message"] = new JsonObject
            {
                ["id"] = id, ["author"] = new JsonObject { ["role"] = role },
                ["create_time"] = time?.ToUnixTimeSeconds(), ["end_turn"] = end, ["metadata"] = metadata
            }
        };
    }

    private static JsonObject Conversation(params JsonObject[] nodes)
    {
        var mapping = new JsonObject();
        foreach (var n in nodes) mapping[n["id"]!.GetValue<string>()] = n;
        return new JsonObject
        {
            ["conversation_id"] = "synthetic-audit",
            ["current_node"] = nodes[^1]["id"]!.GetValue<string>(),
            ["update_time"] = Now.ToUnixTimeSeconds(),
            ["mapping"] = mapping
        };
    }
}
