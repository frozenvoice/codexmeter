using System.Text.Json.Nodes;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class AuditRegressionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void R1_LongGenerationFragments_CountAsOneRequest()
    {
        var parsed = Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-26)),
            Message("analysis", "assistant", "user", Now.AddMinutes(-25), "synthetic-long-request", "gpt-5-6-pro", true, false),
            Message("final", "assistant", "analysis", Now, model: "gpt-5-6-pro")));
        Assert.False(parsed.SchemaMismatch);
        Assert.Single(parsed.Events);
        Assert.Equal(QuotaFamily.GptPro, parsed.Events[0].QuotaFamily);
        Assert.Equal("synthetic-long-request", parsed.Events[0].RequestId);
    }

    [Fact]
    public void R2_GroupPreservesProModelFromHiddenFragment()
    {
        var parsed = Parse(Conversation(
            Message("user", "user", null, Now.AddMinutes(-2)),
            Message("analysis", "assistant", "user", Now.AddMinutes(-1), "synthetic-request", "gpt-5-6-pro", true, false),
            Message("final", "assistant", "analysis", Now, "synthetic-request")));
        Assert.Single(parsed.Events);
        Assert.Equal(QuotaFamily.GptPro, parsed.Events.Single().QuotaFamily);
        Assert.Equal(ModelEvidenceConfidence.ObservedResponse, parsed.Events.Single().ModelConfidence);
    }

    [Fact]
    public void R3_MissingResponseTime_UsesThisInvocationUserTime_NotConversationCreate()
    {
        var parsed = Parse(
            Conversation(
                Message("user", "user", null, Now.AddMinutes(-1)),
                Message("final", "assistant", "user", null, "synthetic-time-request", "gpt-5-6-pro")),
            Now.AddDays(-30));
        Assert.Single(parsed.Events);
        var usage = parsed.Events.Single();
        Assert.Equal(TimestampProvenance.LinkedUserThisInvocation, usage.TimestampProvenance);
        Assert.Equal(Now.AddMinutes(-1).ToUnixTimeSeconds(), usage.CreatedAt.ToUnixTimeSeconds());
        var settings = new AppSettings { ResetAnchorConfigured = true, ResetTimeZoneId = "UTC" };
        var snapshot = new QuotaEngine().Build(
            parsed.Events,
            settings,
            Now,
            Now,
            CompleteCoverage(),
            null,
            AppSyncStatus.UpToDate);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        Assert.NotEqual(CoverageConfidence.Authoritative, snapshot.Coverage.CountConfidence);
    }

    [Fact]
    public void R4_NarrowerRescan_DoesNotDeletePriorEvidence()
    {
        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var db = new SqliteStore(Path.Combine(dir, "synthetic-reconcile.db"), pooling: false);
        var record = new ConversationRecord { ConversationId = "synthetic-reconcile", UpdateTime = Now.ToUnixTimeSeconds() };
        db.ReconcileConversation(record, [Event("synthetic-a"), Event("synthetic-b")]);
        db.ReconcileConversation(record, [Event("synthetic-a")]);
        Assert.Equal(2, db.GetUsageEvents().Count);
        Assert.Contains(db.GetUsageEvents(), e => e.RequestId == "synthetic-b");
    }

    [Fact]
    public void R5_PaginationAndMissingBranch_AreIncomplete()
    {
        var partialMapping = Conversation(
            Message("user", "user", null, Now.AddMinutes(-2)),
            Message("final", "assistant", "user", Now.AddMinutes(-1), "synthetic-known-request", "gpt-5-6-pro"));
        partialMapping["page_info"] = new JsonObject { ["has_previous_page"] = true, ["start_cursor"] = "synthetic-older" };
        partialMapping["mapping"]!["user"]!["children"] = new JsonArray("final", "missing-alternate-branch");
        Assert.False(ConversationDetailLoader.IsCompleteMapping(partialMapping));
        Assert.True(ConversationDetailLoader.HasRemainingPagination(partialMapping));
    }

    [Fact]
    public void R6_CatalogEvidence_SurvivesResolveWithoutTitle()
    {
        var models = new ModelNormalizer();
        models.Observe("synthetic-catalog-pro-model", "GPT-6 Pro");
        Assert.Equal(QuotaFamily.GptPro, models.Resolve(null, "synthetic-catalog-pro-model").Family);
    }

    [Fact]
    public void R7_FreshWeeklyReset_BeatsRetainedAnchor()
    {
        var currentWeekly = Now.AddDays(2);
        var settings = new AppSettings { ResetAnchorConfigured = true };
        var metadata = new QuotaMetadataSet
        {
            SharedProWeekly = new QuotaWindow { Used = 7, Limit = 50, ResetAt = currentWeekly, ObservedAt = Now },
            ProServerStatus = new ProServerStatus { LastConfirmedResetAt = Now.AddDays(-1) }
        };
        var boundary = ProQuotaPeriodResolver.Resolve(settings, Now, metadata.ProServerStatus, currentWeekly, metadata);
        Assert.Equal(currentWeekly, boundary.End);
        Assert.Equal(ResetAnchorSource.Server, boundary.Source);
    }

    [Fact]
    public void R8_DailySolReset_IsNotWeeklyReconstructionAnchor()
    {
        var dailyStatus = new ProServerStatus
        {
            ServerObserved = true,
            ResetConfidence = ServerResetConfidence.Server,
            ResetAt = Now.AddHours(2),
            ModelLimits = [new ProModelLimit { Slug = "gpt-5-6-pro", ResetAt = Now.AddHours(2) }]
        };
        var pro200 = new AppSettings();
        pro200.ApplyPreset(SubscriptionPreset.Pro200);
        var weekly = Now.AddDays(4);
        var dailyBoundary = ProQuotaPeriodResolver.Resolve(pro200, Now, dailyStatus, weekly);
        Assert.Equal(weekly, dailyBoundary.End);
        Assert.NotEqual(dailyStatus.ResetAt, dailyBoundary.End);
    }

    [Fact]
    public void R9_ExpiredAuthoritativeCount_DoesNotCarryIntoNextPeriod()
    {
        var settings = new AppSettings { ResetAnchorConfigured = true };
        var staleWeekly = new QuotaMetadataSet
        {
            SharedProWeekly = new QuotaWindow { Used = 50, Limit = 50, ResetAt = Now.AddMinutes(-1) }
        };
        var staleSnapshot = new QuotaEngine().Build([], settings, Now, Now, CompleteCoverage(), staleWeekly, AppSyncStatus.UpToDate);
        Assert.False(staleSnapshot.UsesServerWeeklyCount);
        Assert.NotEqual(50, staleSnapshot.Used);
        Assert.NotEqual(CoverageConfidence.Authoritative, staleSnapshot.Coverage.CountConfidence);
    }

    private static ParseResult Parse(JsonObject data, DateTimeOffset? fallback = null) =>
        new ConversationParser(new ModelNormalizer(), new MutableClock(Now)).Parse(data,
            new ConversationParseContext { ConversationId = "synthetic-audit", FallbackCreatedAt = fallback });

    private static CoverageInfo CompleteCoverage() => new()
    {
        NormalChats = true,
        ArchivedChats = true,
        Projects = true,
        NormalIndexState = CollectionState.Complete,
        ArchivedIndexState = CollectionState.Complete,
        ProjectsIndexState = CollectionState.Complete
    };

    private static JsonObject Message(string id, string role, string? parent, DateTimeOffset? time,
        string? request = null, string? model = null, bool hidden = false, bool end = true)
    {
        var metadata = new JsonObject();
        if (request is not null) metadata["request_id"] = request;
        if (model is not null) metadata["model_slug"] = model;
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
                ["metadata"] = metadata
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

    private static UsageEvent Event(string key) => new()
    {
        Id = key,
        DedupeKey = UsageEvent.ScopedRequestKey("synthetic-reconcile", key),
        RequestId = key,
        ConversationId = "synthetic-reconcile",
        MessageId = key,
        CreatedAt = Now,
        FirstSeenAt = Now,
        LastSeenAt = Now,
        QuotaFamily = QuotaFamily.GptPro,
        RawModel = "gpt-5-6-pro",
        Source = UsageSource.ConversationSync
    };
}
