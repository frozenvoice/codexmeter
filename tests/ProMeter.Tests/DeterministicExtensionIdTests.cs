using ProMeter.Companion;

namespace ProMeter.Tests;

public class DeterministicExtensionIdTests
{
    // The exact public key committed in extension/manifest.json's "key" field.
    private const string CommittedManifestKey =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAw6/Q09IDARmodxok17l3NIATpSqNWs8DNYfqWcz2fRkwpX4eOdZjAD/uN4HlpXKTCGJp01MY4abBjmijPxE5hKgtqvoZf1VxVMXeHPmojEVmp04bJycyRXcns2J0ebwbB+ORlEo0uHZ+0tGs2IgpIFbAqUdU3yHkdnKLmGD6oMQgEuD4weNTgNNvgfwHTZ86zrKzgKPbXANISwOsNWPJZjd5f1K4YygL4aM92HdIgEymg1SYzLPckMLGlVxMREXzIU0oBiaSJwsgO/YGQHvcCcvQfQ+81I7iJUKCvS/EDabV2VJiaGfszybAqTs5BL5DKPEH3wrBnZjdPmF0HPyFyQIDAQAB";

    // Chromium's own derivation of the above key: SHA-256 of the DER SubjectPublicKeyInfo,
    // first 16 bytes, each nibble mapped 0-9a-f -> a-p. Cross-checked against a known
    // working reference implementation before committing.
    private const string ExpectedExtensionId = "fkmkjahclfbkokccidjbjkafpgimadci";

    [Fact]
    public void CommittedManifestKey_ProducesExpectedDeterministicId()
    {
        Assert.True(ChromiumExtensionId.TryCompute(CommittedManifestKey, out var id));
        Assert.Equal(ExpectedExtensionId, id);
        Assert.Matches("^[a-p]{32}$", id);
    }

    [Fact]
    public void RepoManifest_KeyMatchesTheCommittedExpectedId()
    {
        // Proves the committed extension/manifest.json actually contains the key this
        // test suite verifies, so the constant above can't silently drift from the file.
        var manifestPath = Path.Combine(FindRepoRoot(), "extension", "manifest.json");
        Assert.True(CompanionExtensionManifest.TryReadPublicKey(manifestPath, out var key));
        Assert.Equal(CommittedManifestKey, key);

        Assert.True(CompanionExtensionManifest.TryReadBuiltInExtensionId(manifestPath, out var id));
        Assert.Equal(ExpectedExtensionId, id);
    }

    [Fact]
    public void SameManifestKey_ProducesSameId_RegardlessOfFilesystemPath()
    {
        // The whole point of a manifest "key": an unpacked extension's ID must not depend
        // on which directory it happens to be loaded from (repo\extension vs
        // publish\local\extension).
        var manifestJson = $$"""{"manifest_version":3,"name":"x","version":"1.0.0","key":"{{CommittedManifestKey}}"}""";

        var dirA = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"), "extension");
        var dirB = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"), "publish", "local", "extension");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        var pathA = Path.Combine(dirA, "manifest.json");
        var pathB = Path.Combine(dirB, "manifest.json");
        File.WriteAllText(pathA, manifestJson);
        File.WriteAllText(pathB, manifestJson);

        Assert.True(CompanionExtensionManifest.TryReadBuiltInExtensionId(pathA, out var idA));
        Assert.True(CompanionExtensionManifest.TryReadBuiltInExtensionId(pathB, out var idB));
        Assert.Equal(idA, idB);
        Assert.Equal(ExpectedExtensionId, idA);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!!")]
    public void InvalidKey_FailsClosedInsteadOfThrowing(string? key)
    {
        Assert.False(ChromiumExtensionId.TryCompute(key, out var id));
        Assert.Null(id);
    }

    [Fact]
    public void MissingManifestFile_FailsClosed()
    {
        var missing = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"), "manifest.json");
        Assert.False(CompanionExtensionManifest.TryReadPublicKey(missing, out _));
        Assert.False(CompanionExtensionManifest.TryReadBuiltInExtensionId(missing, out _));
    }

    [Fact]
    public void ManifestWithoutKey_FailsClosed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "manifest.json");
        File.WriteAllText(path, """{"manifest_version":3,"name":"x","version":"1.0.0"}""");

        Assert.False(CompanionExtensionManifest.TryReadPublicKey(path, out _));
        Assert.False(CompanionExtensionManifest.TryReadBuiltInExtensionId(path, out _));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProMeter.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }
}
