using System.Text.Json.Nodes;
using CycleArc.Companion;

namespace CycleArc.Tests;

public class CompanionHostStagingTests : IDisposable
{
    private readonly string _root;

    public CompanionHostStagingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cyclearc-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void StageAndManifest_DoNotPointAtReleaseFolder()
    {
        var fakeRelease = Path.Combine(_root, "fake-release");
        var nativeRoot = Path.Combine(_root, "native-host");
        var source = WriteHost(fakeRelease, "companion-bytes-a");
        var staged = CompanionHostStager.EnsureStaged(source, nativeRoot);
        var identity = CompanionHostStager.ComputeIdentity(source);
        Assert.Equal(Path.Combine(nativeRoot, identity, CompanionHostStager.HostFileName), staged);
        Assert.True(CompanionHostStager.HashesEqual(source, staged));

        var manifest = CompanionHostManifest.TryCreate(staged, "abcdefghijklmnopabcdefghijklmnop", null);
        Assert.True(manifest.Ok);
        Assert.NotNull(manifest.ManifestJson);
        Assert.DoesNotContain(source, manifest.ManifestJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fakeRelease, JsonNode.Parse(manifest.ManifestJson)!["path"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        var path = JsonNode.Parse(manifest.ManifestJson)!["path"]!.GetValue<string>();
        Assert.Equal(Path.GetFullPath(staged), path);
        Assert.Contains(Path.Combine("native-host", identity, CompanionHostStager.HostFileName), path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path.Replace("\\", "\\\\", StringComparison.Ordinal), manifest.ManifestJson, StringComparison.Ordinal);
        Assert.StartsWith("{", manifest.ManifestJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SameContent_ReusesImmutableStagedPath()
    {
        var nativeRoot = Path.Combine(_root, "native-host");
        var source = WriteHost(Path.Combine(_root, "fake-release"), "same-bytes");
        var first = CompanionHostStager.EnsureStaged(source, nativeRoot);
        var stamp = File.GetLastWriteTimeUtc(first);
        var second = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Assert.Equal(first, second);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(second));
        Assert.Equal(CompanionHostStager.ComputeIdentity(source), CompanionHostStager.ComputeIdentity(source));
    }

    [Fact]
    public void ChangedContent_UsesNewStagedPath()
    {
        var nativeRoot = Path.Combine(_root, "native-host");
        var source = WriteHost(Path.Combine(_root, "fake-release"), "version-one");
        var first = CompanionHostStager.EnsureStaged(source, nativeRoot);
        File.WriteAllText(source, "version-two");
        var second = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Assert.NotEqual(first, second);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(CompanionHostStager.HashesEqual(source, second));
        Assert.False(CompanionHostStager.HashesEqual(first, second));
    }

    [Fact]
    public void AdjacentRuntimeFiles_AreCopiedWithoutUiBinaries()
    {
        var fakeRelease = Path.Combine(_root, "fake-release");
        Directory.CreateDirectory(fakeRelease);
        var source = Path.Combine(fakeRelease, CompanionHostStager.HostFileName);
        File.WriteAllText(source, "framework-dependent-host");
        File.WriteAllText(Path.Combine(fakeRelease, "cyclearc-companion-host.dll"), "host-dll");
        File.WriteAllText(Path.Combine(fakeRelease, "cyclearc-companion-host.deps.json"), "{}");
        File.WriteAllText(Path.Combine(fakeRelease, "cyclearc-companion-host.runtimeconfig.json"), "{}");
        File.WriteAllText(Path.Combine(fakeRelease, "CycleArc.Core.dll"), "core");
        File.WriteAllText(Path.Combine(fakeRelease, "cyclearc.exe"), "ui");
        File.WriteAllText(Path.Combine(fakeRelease, "cyclearc-companion-host.pdb"), "symbols");
        var staged = CompanionHostStager.EnsureStaged(source, Path.Combine(_root, "native-host"));
        var stagedDir = Path.GetDirectoryName(staged)!;
        Assert.True(File.Exists(Path.Combine(stagedDir, "cyclearc-companion-host.dll")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "CycleArc.Core.dll")));
        Assert.False(File.Exists(Path.Combine(stagedDir, "cyclearc.exe")));
        Assert.False(File.Exists(Path.Combine(stagedDir, "cyclearc-companion-host.pdb")));
        Assert.Equal(5, CompanionHostStager.RequiredRuntimeFiles(source).Count);
    }

    [Fact]
    public void StaleUnlockedVersions_AreCleaned()
    {
        var nativeRoot = Path.Combine(_root, "native-host");
        var source = WriteHost(Path.Combine(_root, "fake-release"), "v1");
        var first = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(first)!, DateTime.UtcNow.AddDays(-3));
        File.WriteAllText(source, "v2");
        var second = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(second)!, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(source, "v3");
        var third = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.True(File.Exists(third));
    }

    [Fact]
    public void LockedCleanupFailure_IsNonfatal()
    {
        var nativeRoot = Path.Combine(_root, "native-host");
        var source = WriteHost(Path.Combine(_root, "fake-release"), "locked-v1");
        var first = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(first)!, DateTime.UtcNow.AddDays(-3));
        using var locked = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.None);
        File.WriteAllText(source, "locked-v2");
        var second = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Directory.SetLastWriteTimeUtc(Path.GetDirectoryName(second)!, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(source, "locked-v3");
        var third = CompanionHostStager.EnsureStaged(source, nativeRoot);
        Assert.True(File.Exists(first));
        Assert.True(File.Exists(third));
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void RegistrationSource_DoesNotWriteRegistryInCoreHelper()
    {
        var source = File.ReadAllText(Find("src/CycleArc.Core/Companion/CompanionHostStager.cs"));
        Assert.DoesNotContain("Registry", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Win32", source, StringComparison.Ordinal);
    }

    private static string WriteHost(string directory, string contents)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, CompanionHostStager.HostFileName);
        File.WriteAllText(path, contents);
        return path;
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
