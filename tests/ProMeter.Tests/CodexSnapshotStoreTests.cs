using ProMeter.Codex;

namespace ProMeter.Tests;

public class CodexSnapshotStoreTests
{
    [Fact]
    public void ValidSnapshot_SurvivesReload()
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        var snapshot = new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            "plus",
            new DateTimeOffset(2026, 9, 5, 1, 15, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 5, 1, 15, 0, TimeSpan.Zero),
            true,
            null,
            1,
            [
                new CodexQuotaWindow("codex", 42, 300, DateTimeOffset.FromUnixTimeSeconds(1893456000), CodexWindowKind.FiveHour)
            ],
            "ok");
        store.Save(snapshot);
        var loaded = store.Load();
        Assert.NotNull(loaded);
        Assert.Equal(CodexQuotaStatus.Available, loaded.Status);
        Assert.Equal(42, loaded.Windows[0].UsedPercent);
        Assert.Equal(1, loaded.ResetCreditsAvailable);
        Assert.DoesNotContain("@", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("token", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cookie", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransientFailure_PreservesLastGoodAsStale()
    {
        var path = TempFile();
        var store = new CodexSnapshotStore(path);
        store.Save(new CodexQuotaSnapshot(
            CodexQuotaStatus.Available,
            "plus",
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            DateTimeOffset.Parse("2026-09-05T01:00:00Z"),
            true,
            null,
            1,
            [new CodexQuotaWindow(null, 42, 300, null, CodexWindowKind.FiveHour)],
            null));
        var files = new MemoryCodexFileSystem();
        files.PathFolders.Add(@"C:\Tools");
        files.Files.Add(@"C:\Tools\codex.exe");
        var factory = new ScriptedCodexProcessFactory { Responder = _ => [], ResponseDelay = TimeSpan.FromSeconds(60) };
        var service = new CodexQuotaService(
            new CodexExecutableLocator(files),
            new CodexAppServerClient(factory),
            store,
            "1.0.0");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await service.RefreshAsync(@"C:\Tools\codex.exe", cts.Token);
        Assert.True(result.UsedCache);
        Assert.Equal(42, result.Snapshot.Windows[0].UsedPercent);
        Assert.True(result.Snapshot.Status is CodexQuotaStatus.Stale or CodexQuotaStatus.Cancelled);
        Assert.NotEqual(0, result.Snapshot.Windows[0].UsedPercent);
    }

    [Fact]
    public void Unavailable_IsNotDisplayedAsZero()
    {
        var snapshot = CodexQuotaSnapshot.Empty(CodexQuotaStatus.Unavailable, "missing");
        Assert.False(snapshot.HasUsablePercentages);
        Assert.Equal("?", CodexDisplayFormatting.PercentText(null));
        Assert.DoesNotContain("0%", CodexDisplayFormatting.OverviewText(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public void ForbiddenPayload_IsRejected()
    {
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"email":"user@example.com"}"""));
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"Authorization":"Bearer abc"}"""));
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"access_token":"x"}"""));
        Assert.True(CodexSnapshotStore.ContainsForbiddenPayload("""{"accountId":"must-not-be-persisted"}"""));
        Assert.False(CodexSnapshotStore.ContainsForbiddenPayload("""{"status":"Available","usedPercent":42}"""));
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"prometer-codex-{Guid.NewGuid():N}.json");
}
