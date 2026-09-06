using System.Security.Cryptography;
using ProMeter.Services;

namespace ProMeter.Companion;

public static class CompanionHostStager
{
    public const string HostFileName = "prometer-companion-host.exe";
    public const int IdentityLength = 16;
    public const int KeepVersions = 2;

    public static string DefaultRoot => AppPaths.NativeHostRoot;

    public static string EnsureStaged(string sourceHostPath, string? nativeHostRoot = null)
    {
        var source = Path.GetFullPath(sourceHostPath);
        if (!File.Exists(source)
            || !string.Equals(Path.GetFileName(source), HostFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("prometer-companion-host.exe is missing", sourceHostPath);
        }

        var root = Path.GetFullPath(nativeHostRoot ?? DefaultRoot);
        Directory.CreateDirectory(root);
        var identity = ComputeIdentity(source);
        var finalDir = Path.Combine(root, identity);
        var finalHost = Path.Combine(finalDir, HostFileName);
        if (File.Exists(finalHost) && HashesEqual(source, finalHost))
        {
            CleanupOld(root, identity);
            return finalHost;
        }

        var tempDir = Path.Combine(root, ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            foreach (var file in RequiredRuntimeFiles(source))
            {
                File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), overwrite: false);
            }

            var stagedTemp = Path.Combine(tempDir, HostFileName);
            if (!File.Exists(stagedTemp) || !HashesEqual(source, stagedTemp))
            {
                throw new IOException("staged companion host hash mismatch");
            }

            if (Directory.Exists(finalDir))
            {
                if (File.Exists(finalHost) && HashesEqual(source, finalHost))
                {
                    return finalHost;
                }

                TryDeleteDirectory(finalDir);
            }

            if (!Directory.Exists(finalDir))
            {
                Directory.Move(tempDir, finalDir);
                tempDir = "";
            }

            if (!File.Exists(finalHost) || !HashesEqual(source, finalHost))
            {
                throw new IOException("staged companion host is incomplete");
            }

            return finalHost;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
            CleanupOld(root, identity);
        }
    }

    public static string ComputeIdentity(string sourceHostPath)
    {
        var hash = HashFile(sourceHostPath);
        return hash.Length <= IdentityLength ? hash : hash[..IdentityLength];
    }

    public static IReadOnlyList<string> RequiredRuntimeFiles(string sourceHostPath)
    {
        var source = Path.GetFullPath(sourceHostPath);
        var directory = Path.GetDirectoryName(source) ?? "";
        var files = new List<string> { source };
        foreach (var name in new[]
        {
            "prometer-companion-host.dll",
            "prometer-companion-host.deps.json",
            "prometer-companion-host.runtimeconfig.json",
            "ProMeter.Core.dll"
        })
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                files.Add(candidate);
            }
        }

        return files;
    }

    public static bool HashesEqual(string leftPath, string rightPath) =>
        string.Equals(HashFile(leftPath), HashFile(rightPath), StringComparison.Ordinal);

    public static void CleanupOld(string nativeHostRoot, string currentIdentity, int keepVersions = KeepVersions)
    {
        if (!Directory.Exists(nativeHostRoot))
        {
            return;
        }

        foreach (var leftover in Directory.GetDirectories(nativeHostRoot, ".tmp-*"))
        {
            TryDeleteDirectory(leftover);
        }

        var keep = Math.Max(1, keepVersions);
        var versions = Directory.GetDirectories(nativeHostRoot)
            .Select(path => new DirectoryInfo(path))
            .Where(dir => IsIdentityName(dir.Name))
            .OrderByDescending(dir => string.Equals(dir.Name, currentIdentity, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(dir => dir.LastWriteTimeUtc)
            .ToList();

        foreach (var extra in versions.Skip(keep))
        {
            TryDeleteDirectory(extra.FullName);
        }
    }

    public static bool IsIdentityName(string name) =>
        name.Length == IdentityLength && name.All(ch => char.IsAsciiHexDigit(ch));

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
