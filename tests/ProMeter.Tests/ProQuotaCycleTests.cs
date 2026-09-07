using ProMeter.Services;

namespace ProMeter.Tests;

public class ProQuotaCycleTests
{
    private static readonly DateTimeOffset T = new(2026, 9, 6, 5, 20, 0, TimeSpan.Zero);

    [Fact]
    public void A_BeforeReset_CurrentCycleIsPreviousWeek()
    {
        var now = T.AddHours(-2);
        var period = ProQuotaPeriodResolver.Resolve(Settings(), now, Live(T));
        Assert.Equal(T.AddDays(-7), period.Start);
        Assert.Equal(T, period.End);
        Assert.Equal(ResetAnchorSource.Server, period.Source);
        Assert.False(period.NextResetEstimated);
    }

    [Fact]
    public void B_AfterCrossingT_CurrentCycleStartsAtT()
    {
        var now = T.AddHours(3);
        var period = ProQuotaPeriodResolver.Resolve(Settings(), now, Live(T));
        Assert.Equal(T, period.Start);
        Assert.Equal(T.AddDays(7), period.End);
        Assert.True(period.NextResetEstimated);
    }

    [Fact]
    public void C_MissingResetAt_RetainsAnchor()
    {
        var now = T.AddHours(3);
        var status = new ProServerStatus
        {
            ServerObserved = true,
            ResetAt = null,
            ResetConfidence = ServerResetConfidence.None,
            LastConfirmedResetAt = T
        };
        var period = ProQuotaPeriodResolver.Resolve(Settings(), now, status);
        Assert.Equal(T, period.Start);
        Assert.Equal(T.AddDays(7), period.End);
        Assert.Equal(ResetAnchorSource.RetainedServer, period.Source);
        Assert.True(period.CurrentCycleKnown);
    }

    [Fact]
    public void D_Disconnected_RetainedAnchorStillScopesCount()
    {
        var now = T.AddHours(3);
        var events = new[]
        {
            Pro("pre-a", T.AddMinutes(-1)),
            Pro("pre-b", T.AddMinutes(-1)),
            Pro("post-a", T.AddMinutes(1)),
            Pro("post-b", T.AddHours(1))
        };
        var snapshot = Engine(events, now, new ProServerStatus
        {
            ServerObserved = false,
            RestrictionState = ProRestrictionState.Unknown,
            LastConfirmedResetAt = T
        });
        Assert.Equal(2, snapshot.ReconstructedUsed);
        Assert.Equal(ResetAnchorSource.RetainedServer, snapshot.ResetAnchorSource);
        Assert.True(snapshot.CurrentCycleKnown);
        Assert.Equal(UiText.ReconstructedCount(2), ProStatusPresentation.From(snapshot).ConfirmedRequestsText);
    }

    [Fact]
    public void E_PreResetEvents_AreExcludedAfterT()
    {
        var now = T.AddHours(3);
        var snapshot = Engine(
            [Pro("pre", T.AddMinutes(-1)), Pro("pre2", T.AddMinutes(-1))],
            now,
            Live(T));
        Assert.Equal(0, snapshot.ReconstructedUsed);
        Assert.True(snapshot.CurrentCycleKnown);
    }

    [Fact]
    public void F_PostResetEvents_IncrementLowerBound()
    {
        var now = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);
        var events = new[]
        {
            Pro("pre-a", T.AddMinutes(-1)),
            Pro("pre-b", T.AddMinutes(-1)),
            Pro("post-a", T.AddMinutes(1)),
            Pro("post-b", now.AddHours(-1))
        };
        var snapshot = Engine(events, now, Live(T));
        Assert.Equal(2, snapshot.ReconstructedUsed);
        Assert.DoesNotContain(events.Where(e => e.CreatedAt < T), e => QuotaPeriodCalculator.InRange(e.CreatedAt, snapshot.PeriodStart, snapshot.PeriodEnd));
    }

    [Fact]
    public void G_MoreThanSevenDays_AdvancesWholeWeeks()
    {
        var now = T.AddDays(8);
        var period = ProQuotaPeriodResolver.Resolve(
            Settings(),
            now,
            new ProServerStatus { LastConfirmedResetAt = T });
        Assert.Equal(T.AddDays(7), period.Start);
        Assert.Equal(T.AddDays(14), period.End);
        Assert.True(QuotaPeriodCalculator.InRange(now, period.Start, period.End));
    }

    [Fact]
    public void H_FreshAuthoritativeT2_ReplacesRetainedAnchor()
    {
        var t2 = T.AddDays(7);
        var now = t2.AddHours(1);
        var incoming = Live(t2);
        ProServerStatus.RetainConfirmedReset(incoming, new ProServerStatus { LastConfirmedResetAt = T });
        Assert.Equal(t2, incoming.LastConfirmedResetAt);
        var period = ProQuotaPeriodResolver.Resolve(Settings(), now, incoming);
        Assert.Equal(t2, period.Start);
        Assert.Equal(t2.AddDays(7), period.End);
        Assert.Equal(ResetAnchorSource.Server, period.Source);
    }

    [Fact]
    public void I_NoAnchor_UsableReconstructionShowsAsEstimatedNotUnavailable()
    {
        var now = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);
        var snapshot = Engine([Pro("old", now.AddDays(-1))], now, ProServerStatus.Unknown());
        Assert.False(snapshot.CurrentCycleKnown);
        Assert.Equal(ResetAnchorSource.Default, snapshot.ResetAnchorSource);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var presentation = ProStatusPresentation.From(snapshot);
            // An unconfirmed cycle boundary must not hide a usable reconstructed count
            // behind "Unavailable" - it shows as an explicit estimate instead.
            Assert.Equal(UiText.ReconstructedCount(1), presentation.ConfirmedRequestsText);
            Assert.DoesNotContain("확인 불가", presentation.ConfirmedRequestsText, StringComparison.Ordinal);
            Assert.Equal("이번 주기 확인 사용", UiText.ConfirmedProUsage);
            var display = DisplayFormatting.ResetDisplay(snapshot);
            Assert.Equal(UiText.NotConfirmed, display.TimeValue);
            Assert.Null(display.EstimateLabel);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void J_UserConfiguredAnchor_WorksWithoutServer()
    {
        var settings = Settings();
        settings.ResetAnchorConfigured = true;
        settings.ResetTimeZoneId = "UTC";
        settings.ResetWeekday = DayOfWeek.Monday;
        settings.ResetTime = TimeSpan.Zero;
        var now = new DateTimeOffset(2026, 9, 6, 8, 0, 0, TimeSpan.Zero);
        var snapshot = new QuotaEngine().Build(
            [Pro("in-window", now.AddHours(-1))],
            settings,
            now,
            now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet(),
            AppSyncStatus.UpToDate);
        Assert.True(snapshot.CurrentCycleKnown);
        Assert.Equal(ResetAnchorSource.UserConfigured, snapshot.ResetAnchorSource);
        Assert.Equal(1, snapshot.ReconstructedUsed);
        Assert.Equal(UiText.ReconstructedCount(1), ProStatusPresentation.From(snapshot).ConfirmedRequestsText);
    }

    [Fact]
    public void Recover_RestrictionNotificationKey_RestoresLostAnchor()
    {
        var status = new ProServerStatus
        {
            ServerObserved = true,
            ResetAt = null,
            ResetConfidence = ServerResetConfidence.None
        };
        var settings = Settings();
        settings.LastNotifiedProRestrictionKey = "CorrelatedRestriction:2026-09-06T05:20:14.0481910+00:00";
        Assert.True(ProServerStatus.TryRecoverLastConfirmedFromRestrictionKey(status, settings.LastNotifiedProRestrictionKey));
        Assert.Equal(
            DateTimeOffset.Parse("2026-09-06T05:20:14.0481910+00:00", CultureInfo.InvariantCulture),
            status.LastConfirmedResetAt);

        var path = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"), "status.json");
        var service = new ProServerStatusService(new ProServerStatusStore(path));
        Assert.True(service.RecoverLastConfirmedFromSettings(settings));
        Assert.Equal(status.LastConfirmedResetAt, service.Current.LastConfirmedResetAt);
        var reloaded = new ProServerStatusStore(path).Load();
        Assert.Equal(status.LastConfirmedResetAt, reloaded?.LastConfirmedResetAt);
        Assert.False(service.RecoverLastConfirmedFromSettings(settings));
    }

    [Fact]
    public void Persist_LastConfirmedReset_SurvivesNullResetRefresh()
    {
        var path = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"), "status.json");
        var store = new ProServerStatusStore(path);
        store.Save(new ProServerStatus
        {
            ServerObserved = true,
            ResetAt = T,
            ResetConfidence = ServerResetConfidence.Server,
            LastConfirmedResetAt = T
        });

        var loaded = store.Load();
        Assert.Equal(T, loaded?.LastConfirmedResetAt);
        var incoming = new ProServerStatus
        {
            ServerObserved = true,
            ResetAt = null,
            ResetConfidence = ServerResetConfidence.None
        };
        ProServerStatus.RetainConfirmedReset(incoming, loaded);
        Assert.Null(incoming.ResetAt);
        Assert.Equal(T, incoming.LastConfirmedResetAt);
        store.Save(incoming);
        var again = store.Load();
        Assert.Null(again?.ResetAt);
        Assert.Equal(T, again?.LastConfirmedResetAt);
    }

    [Fact]
    public void Migrate_OldStoredServerReset_BecomesLastConfirmed()
    {
        var path = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"), "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
            {"version":1,"serverObserved":true,"restrictionState":"Unknown","resetAt":"2026-09-06T05:20:00+00:00","resetConfidence":"Server"}
            """);
        var loaded = new ProServerStatusStore(path).Load();
        Assert.Equal(T, loaded?.LastConfirmedResetAt);
    }

    [Fact]
    public void RetainedDisplay_DoesNotShowFallbackMonday()
    {
        var now = T.AddHours(3);
        var snapshot = Engine([Pro("post", T.AddMinutes(40))], now, new ProServerStatus
        {
            ServerObserved = true,
            ResetAt = null,
            LastConfirmedResetAt = T
        });
        UiText.SetLanguage(UiLanguage.Korean);
        try
        {
            var display = DisplayFormatting.ResetDisplay(snapshot);
            Assert.Equal("주기 시작", display.TimeLabel);
            Assert.Equal(DisplayFormatting.FormatStamp(T), display.TimeValue);
            Assert.Equal("다음 리셋", display.EstimateLabel);
            Assert.Contains(DisplayFormatting.FormatStamp(T.AddDays(7)), display.EstimateValue, StringComparison.Ordinal);
            Assert.Contains("추정", display.EstimateValue, StringComparison.Ordinal);
            Assert.DoesNotContain("추정 기준", display.EstimateLabel, StringComparison.Ordinal);
        }
        finally
        {
            UiText.SetLanguage(UiLanguage.English);
        }
    }

    [Fact]
    public void QuotaEngineAndResolver_ShareTheSameWindow()
    {
        var now = T.AddHours(3);
        var status = new ProServerStatus { LastConfirmedResetAt = T };
        var resolved = ProQuotaPeriodResolver.Resolve(Settings(), now, status);
        var snapshot = Engine([Pro("post", T.AddMinutes(1))], now, status);
        Assert.Equal(resolved.Start, snapshot.PeriodStart);
        Assert.Equal(resolved.End, snapshot.PeriodEnd);
        Assert.Equal(resolved.Source, snapshot.ResetAnchorSource);
    }

    [Fact]
    public void AppStartup_RetiresCompanionAndNeverStartsHistoryCollection()
    {
        var app = File.ReadAllText(Find("src/ProMeter/App.xaml.cs"));
        Assert.Contains("LegacyCompanionCleanup.Unregister", app, StringComparison.Ordinal);
        Assert.DoesNotContain("_companionServer.Start", app, StringComparison.Ordinal);
        Assert.DoesNotContain("new SqliteStore", app, StringComparison.Ordinal);
    }

    private static QuotaSnapshot Engine(IReadOnlyList<UsageEvent> events, DateTimeOffset now, ProServerStatus status) =>
        new QuotaEngine().Build(
            events,
            Settings(),
            now,
            now,
            new CoverageInfo { NormalChats = true },
            new QuotaMetadataSet { ProServerStatus = status },
            AppSyncStatus.UpToDate);

    private static AppSettings Settings()
    {
        var settings = AppSettings.CreateDefaults();
        settings.ResetTimeZoneId = "Korea Standard Time";
        settings.ResetWeekday = DayOfWeek.Monday;
        settings.ResetTime = TimeSpan.Zero;
        return settings;
    }

    private static ProServerStatus Live(DateTimeOffset reset) => new()
    {
        ServerObserved = true,
        ResetAt = reset,
        ResetConfidence = ServerResetConfidence.Server,
        LastConfirmedResetAt = reset
    };

    private static UsageEvent Pro(string id, DateTimeOffset created) => new()
    {
        Id = id,
        RequestId = id,
        ConversationId = "c-" + id,
        MessageId = id,
        CreatedAt = created,
        NormalizedModel = "GPT-5.4 Pro",
        RawModel = "gpt-5-4-pro",
        Source = UsageSource.ConversationSync,
        QuotaFamily = QuotaFamily.GptPro
    };

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
