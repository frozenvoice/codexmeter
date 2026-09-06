using Microsoft.Data.Sqlite;
using ProMeter.Codex;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ConversationFetchBackoffTests
{
    [Fact]
    public void DelaySchedule_IsBounded()
    {
        Assert.Equal(TimeSpan.Zero, ConversationFetchBackoff.DelayAfterFailures(0));
        Assert.Equal(TimeSpan.FromMinutes(15), ConversationFetchBackoff.DelayAfterFailures(1));
        Assert.Equal(TimeSpan.FromHours(1), ConversationFetchBackoff.DelayAfterFailures(2));
        Assert.Equal(TimeSpan.FromHours(6), ConversationFetchBackoff.DelayAfterFailures(3));
        Assert.Equal(TimeSpan.FromHours(24), ConversationFetchBackoff.DelayAfterFailures(4));
        Assert.Equal(TimeSpan.FromHours(24), ConversationFetchBackoff.DelayAfterFailures(12));
    }

    [Fact]
    public void ShouldFetch_RespectsForceChangeSuccessBackoffAndParserVersion()
    {
        var now = DateTimeOffset.Parse("2026-09-06T00:00:00Z");
        var item = new ConversationIndexItem { Id = "conv-a", UpdateTime = 1_000 };
        var failed = new ConversationRecord
        {
            ConversationId = "conv-a",
            LastAttemptedUpdateTime = 1_000,
            ConsecutiveFetchFailures = 1,
            NextEligibleFetchAt = now.AddMinutes(15),
            FetchFailureParserVersion = ConversationFetchBackoff.ParserCompatibilityVersion,
            Status = ConversationScanStatus.SchemaMismatch,
            LastFetchFailureCategory = ConversationFetchBackoff.SchemaMismatch
        };

        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now, forceBodyRescan: true));
        Assert.False(ConversationFetchBackoff.ShouldFetch(item, failed, now, forceBodyRescan: false));
        Assert.True(ConversationFetchBackoff.IsDeferredFailure(item, failed, now, false));

        item.UpdateTime = 1_200;
        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now, false));
        Assert.False(ConversationFetchBackoff.IsDeferredFailure(item, failed, now, false));
        item.UpdateTime = 1_000;

        failed.FetchFailureParserVersion = 0;
        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now, false));
        failed.FetchFailureParserVersion = ConversationFetchBackoff.ParserCompatibilityVersion;

        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now.AddMinutes(15), false));

        var ok = new ConversationRecord
        {
            ConversationId = "conv-a",
            LastSeenUpdateTime = 1_000,
            LastAttemptedUpdateTime = 1_000,
            LastSuccessfulScan = now,
            Status = ConversationScanStatus.Ok
        };
        Assert.False(ConversationFetchBackoff.ShouldFetch(item, ok, now, false));
        Assert.False(ConversationFetchBackoff.IsDeferredFailure(item, ok, now, false));
    }

    [Fact]
    public void MissingUpdateTime_RespectsActiveFailureBackoff()
    {
        var now = DateTimeOffset.Parse("2026-09-06T00:00:00Z");
        var item = new ConversationIndexItem { Id = "conv-bad", UpdateTime = 0 };
        Assert.True(ConversationFetchBackoff.ShouldFetch(item, existing: null, now, forceBodyRescan: false));
        Assert.False(ConversationFetchBackoff.RemoteChanged(item, new ConversationRecord { LastAttemptedUpdateTime = 0, LastSeenUpdateTime = 0 }));

        var failed = new ConversationRecord
        {
            ConversationId = "conv-bad",
            ConsecutiveFetchFailures = 1,
            NextEligibleFetchAt = now.AddMinutes(15),
            FetchFailureParserVersion = ConversationFetchBackoff.ParserCompatibilityVersion,
            Status = ConversationScanStatus.SchemaMismatch,
            LastFetchFailureCategory = ConversationFetchBackoff.SchemaMismatch
        };
        Assert.False(ConversationFetchBackoff.ShouldFetch(item, failed, now.AddMinutes(1), false));
        Assert.True(ConversationFetchBackoff.IsDeferredFailure(item, failed, now.AddMinutes(1), false));
        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now.AddMinutes(15), false));
        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now.AddMinutes(1), forceBodyRescan: true));

        failed.FetchFailureParserVersion = 0;
        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now.AddMinutes(1), false));
        failed.FetchFailureParserVersion = ConversationFetchBackoff.ParserCompatibilityVersion;

        failed.ConsecutiveFetchFailures = 2;
        failed.NextEligibleFetchAt = now.AddMinutes(15) + TimeSpan.FromHours(1);
        Assert.False(ConversationFetchBackoff.ShouldFetch(item, failed, now.AddMinutes(16), false));
        Assert.True(ConversationFetchBackoff.ShouldFetch(item, failed, now.AddMinutes(15) + TimeSpan.FromHours(1), false));
    }

    [Fact]
    public void RemoteChanged_RequiresPositiveUpdateTime()
    {
        var existing = new ConversationRecord { LastSeenUpdateTime = 1_000, LastAttemptedUpdateTime = 1_000 };
        Assert.False(ConversationFetchBackoff.RemoteChanged(new ConversationIndexItem { UpdateTime = 0 }, existing));
        Assert.False(ConversationFetchBackoff.RemoteChanged(new ConversationIndexItem { UpdateTime = 1_000 }, existing));
        Assert.True(ConversationFetchBackoff.RemoteChanged(new ConversationIndexItem { UpdateTime = 1_200 }, existing));
    }

    [Fact]
    public void SqliteMigration_AddsBackoffColumnsWithoutDestroyingRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE conversations (
                    conversation_id TEXT PRIMARY KEY,
                    update_time REAL NOT NULL DEFAULT 0,
                    project_id TEXT,
                    archived INTEGER NOT NULL DEFAULT 0,
                    last_scanned TEXT,
                    last_seen_update_time REAL NOT NULL DEFAULT 0,
                    source TEXT,
                    last_successful_scan TEXT,
                    last_error_at TEXT,
                    last_error TEXT,
                    scan_status TEXT
                );
                INSERT INTO conversations(
                    conversation_id, update_time, last_seen_update_time, source, last_successful_scan, scan_status)
                VALUES('conv-keep', 100, 100, 'chat', '2026-09-01T00:00:00.0000000+00:00', 'Ok');
                """;
            command.ExecuteNonQuery();
        }

        using var store = new SqliteStore(path);
        var record = store.GetConversation("conv-keep");
        Assert.Equal(100, record?.LastSeenUpdateTime);
        Assert.Equal(ConversationScanStatus.Ok, record?.Status);
        Assert.Equal(0, record?.ConsecutiveFetchFailures);
        Assert.Null(record?.NextEligibleFetchAt);
        Assert.Null(record?.LastFetchFailureCategory);
        Assert.Equal(0, record?.LastAttemptedUpdateTime);
        Assert.Equal(0, record?.FetchFailureParserVersion);
        Assert.NotNull(record?.LastSuccessfulScan);
    }

    [Fact]
    public void RecordFailure_ThenSuccess_ResetsBackoff()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "reset.db"));
        var now = DateTimeOffset.Parse("2026-09-06T00:00:00Z");
        var item = new ConversationIndexItem { Id = "conv-reset", UpdateTime = 50 };
        store.RecordConversationFailure(
            null,
            item,
            ConversationScanStatus.FetchFailed,
            "conversation body timed out",
            ConversationFetchBackoff.BodyTimeout,
            now);
        var failed = store.GetConversation(item.Id);
        Assert.Equal(1, failed?.ConsecutiveFetchFailures);
        Assert.Equal(now.AddMinutes(15), failed?.NextEligibleFetchAt);
        Assert.Equal(ConversationFetchBackoff.BodyTimeout, failed?.LastFetchFailureCategory);
        Assert.Equal(ConversationFetchBackoff.ParserCompatibilityVersion, failed?.FetchFailureParserVersion);
        Assert.Equal(50, failed?.LastAttemptedUpdateTime);

        store.ReconcileConversation(new ConversationRecord
        {
            ConversationId = item.Id,
            UpdateTime = 50,
            LastSeenUpdateTime = 50,
            LastAttemptedUpdateTime = 50,
            LastSuccessfulScan = now.AddMinutes(1),
            LastScanned = now.AddMinutes(1),
            Status = ConversationScanStatus.Ok,
            ConsecutiveFetchFailures = 0,
            Source = "chat"
        }, []);
        var ok = store.GetConversation(item.Id);
        Assert.Equal(0, ok?.ConsecutiveFetchFailures);
        Assert.Null(ok?.NextEligibleFetchAt);
        Assert.Null(ok?.LastFetchFailureCategory);
        Assert.Equal(ConversationScanStatus.Ok, ok?.Status);
        Assert.Equal(50, ok?.LastSeenUpdateTime);
    }

    [Fact]
    public async Task ManualIncremental_RespectsBackoff_UnlessRemoteChangedOrForced()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-09-06T00:00:00Z"));
        var (engine, store, provider, settings, item) = CreateMismatch(clock);
        settings.BodyFetchDelayMilliseconds = 0;

        var first = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, first.Status);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.DeferredCount);

        var second = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.PartialData, second.Status);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.Equal(CollectionState.Partial, engine.LastCoverage.OverallState);
        Assert.Contains("conversations not applied", DisplayFormatting.CoverageCompactLabel(engine.LastCoverage), StringComparison.OrdinalIgnoreCase);

        item.UpdateTime += 90;
        var changed = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, changed.Status);

        var forced = await engine.SyncAsync(provider, settings, new SyncRunOptions
        {
            BypassPause = true,
            ForceBodyRescan = true,
            Origin = SyncOrigin.Manual
        });
        Assert.Equal(3, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, forced.Status);

        clock.UtcNow = clock.UtcNow.AddHours(7);
        var expired = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(4, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, expired.Status);
        Assert.Equal(4, store.GetConversation(item.Id)?.ConsecutiveFetchFailures);
        Assert.Equal(clock.UtcNow.AddHours(24), store.GetConversation(item.Id)?.NextEligibleFetchAt);
    }

    [Fact]
    public async Task MissingUpdateTime_FailedConversation_IsDeferredUntilBackoffExpires()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-09-06T00:00:00Z"));
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "zero-update.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-bad", UpdateTime = 0, CreateTime = 0 };
        fixture.AddConversation(item, new ConversationLoadResult
        {
            Complete = false,
            SchemaMismatch = true,
            Diagnostics = ["mapping incomplete"]
        });
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;

        var first = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, first.Status);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(1, store.GetConversation(item.Id)?.ConsecutiveFetchFailures);
        Assert.Equal(clock.UtcNow.AddMinutes(15), store.GetConversation(item.Id)?.NextEligibleFetchAt);

        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var deferred = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(1, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.PartialData, deferred.Status);
        Assert.Equal(1, engine.LastCoverage.FailedConversations);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.True(engine.LastCoverage.ConversationIncomplete);
        Assert.Equal(CollectionState.Partial, engine.LastCoverage.OverallState);
        Assert.NotEqual(AppSyncStatus.UpToDate, engine.LastStatus);

        clock.UtcNow = DateTimeOffset.Parse("2026-09-06T00:15:00Z");
        var expired = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, expired.Status);
        Assert.Equal(2, store.GetConversation(item.Id)?.ConsecutiveFetchFailures);
        Assert.Equal(clock.UtcNow.AddHours(1), store.GetConversation(item.Id)?.NextEligibleFetchAt);

        var skippedDuringHour = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(2, provider.BodyFetches);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.DeferredCount);

        var forced = await engine.SyncAsync(provider, settings, new SyncRunOptions
        {
            BypassPause = true,
            ForceBodyRescan = true,
            Origin = SyncOrigin.Manual
        });
        Assert.Equal(3, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, forced.Status);
        Assert.Equal(3, store.GetConversation(item.Id)?.ConsecutiveFetchFailures);
        Assert.Equal(clock.UtcNow.AddHours(6), store.GetConversation(item.Id)?.NextEligibleFetchAt);
    }

    [Fact]
    public async Task TimeoutAndTwoSchemaMismatches_StayPartialWhenDeferred()
    {
        var clock = new MutableClock(DateTimeOffset.Parse("2026-09-06T00:00:00Z"));
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var store = new SqliteStore(Path.Combine(dir, "summary.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var now = clock.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-timeout", UpdateTime = now, CreateTime = now - 10 }, ConversationFixtures.NormalPro("conv-timeout", now));
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-schema-a", UpdateTime = now - 1, CreateTime = now - 20 }, ConversationFixtures.NormalPro("conv-schema-a", now - 1));
        fixture.AddConversation(new ConversationIndexItem { Id = "conv-schema-b", UpdateTime = now - 2, CreateTime = now - 30 }, ConversationFixtures.NormalPro("conv-schema-b", now - 2));
        fixture.LoadOverride = id => id == "conv-timeout"
            ? throw new ChatGptProviderException(CompanionBridgeProtocol.TimeoutError)
            : new ConversationLoadResult { Complete = false, SchemaMismatch = true, Diagnostics = ["mapping incomplete"] };
        var provider = new IncrementalSyncTests.CountingProvider(fixture);
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        settings.BodyFetchDelayMilliseconds = 0;

        var first = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(AppSyncStatus.ProviderSchemaMismatch, first.Status);
        Assert.Equal(3, provider.BodyFetches);
        Assert.Equal(3, engine.LastCoverage.FailedConversations);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
        Assert.Equal(2, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(CollectionState.Partial, engine.LastCoverage.OverallState);

        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("대화 3개 읽기 실패", DisplayFormatting.CoverageCompactLabel(engine.LastCoverage));
            var details = string.Join('\n', DisplayFormatting.CoverageFailureDetailLines(engine.LastCoverage));
            Assert.Contains("읽기 시간 초과", details, StringComparison.Ordinal);
            Assert.Contains("응답 형식 불일치", details, StringComparison.Ordinal);
            Assert.Contains("이번 동기화 실패    3", details, StringComparison.Ordinal);
            Assert.Contains("재시도 대기    0", details, StringComparison.Ordinal);
            Assert.Contains("최소값", details, StringComparison.Ordinal);
            Assert.DoesNotContain("SchemaMismatch", details, StringComparison.Ordinal);
            Assert.DoesNotContain("BodyTimeout", details, StringComparison.Ordinal);
            Assert.DoesNotContain("conv-timeout", details, StringComparison.Ordinal);
            Assert.DoesNotContain("HTTP status", details, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }

        var second = await engine.SyncAsync(provider, settings, SyncRunOptions.ManualIncremental);
        Assert.Equal(3, provider.BodyFetches);
        Assert.Equal(AppSyncStatus.PartialData, second.Status);
        Assert.Equal(3, engine.LastCoverage.FailedConversations);
        Assert.Equal(0, engine.LastCoverage.FailureSummary.FailedThisSyncCount);
        Assert.Equal(3, engine.LastCoverage.FailureSummary.DeferredCount);
        Assert.Equal(1, engine.LastCoverage.FailureSummary.BodyTimeoutCount);
        Assert.Equal(2, engine.LastCoverage.FailureSummary.SchemaMismatchCount);
        Assert.Equal(CollectionState.Partial, engine.LastCoverage.OverallState);
        Assert.NotEqual(AppSyncStatus.UpToDate, engine.LastStatus);
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("대화 3개 미반영", DisplayFormatting.CoverageCompactLabel(engine.LastCoverage));
            var deferred = string.Join('\n', DisplayFormatting.CoverageFailureDetailLines(engine.LastCoverage));
            Assert.Contains("이번 동기화 실패    0", deferred, StringComparison.Ordinal);
            Assert.Contains("재시도 대기    3", deferred, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }

        Assert.Equal("3 conversations not applied", DisplayFormatting.CoverageCompactLabel(engine.LastCoverage));
    }

    [Fact]
    public void AdditionalPartialCauses_PrefixCompactLabel()
    {
        var coverage = new CoverageInfo
        {
            IndexIncomplete = true,
            FailedConversations = 3,
            FailureSummary =
            {
                FailedThisSyncCount = 3,
                BodyTimeoutCount = 1,
                SchemaMismatchCount = 2
            }
        };
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            Assert.Equal("일부 미반영 · 대화 3개 읽기 실패", DisplayFormatting.CoverageCompactLabel(coverage));
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    private static (SyncEngine Engine, SqliteStore Store, IncrementalSyncTests.CountingProvider Provider, AppSettings Settings, ConversationIndexItem Item) CreateMismatch(IClock clock)
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteStore(Path.Combine(dir, "backoff.db"));
        var models = new ModelNormalizer();
        var engine = new SyncEngine(store, new ConversationParser(models), models, new AppLog(Path.Combine(dir, "logs")), clock);
        var now = clock.UtcNow.ToUnixTimeSeconds();
        var fixture = new FixtureChatGptProvider();
        var item = new ConversationIndexItem { Id = "conv-bad", UpdateTime = now, CreateTime = now - 30 };
        fixture.AddConversation(item, new ConversationLoadResult
        {
            Complete = false,
            SchemaMismatch = true,
            Diagnostics = ["mapping incomplete"]
        });
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "UTC";
        return (engine, store, new IncrementalSyncTests.CountingProvider(fixture), settings, item);
    }
}
