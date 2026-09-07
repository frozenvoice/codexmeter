using CodexMeter.Codex;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class ProductIdentityTests
{
    [Fact]
    public void AppServerHandshakeUsesCurrentProductIdentity()
    {
        var request = JsonNode.Parse(CodexProtocol.BuildInitialize("1.2.3"))!;
        var client = request["params"]!["clientInfo"]!;
        Assert.Equal("codexmeter", client["name"]!.GetValue<string>());
        Assert.Equal("CodexMeter", client["title"]!.GetValue<string>());
        Assert.Equal("1.2.3", client["version"]!.GetValue<string>());
        Assert.Equal("CodexMeter.Core", typeof(CodexProtocol).Assembly.GetName().Name);
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Korean)]
    public void LocalizedProductLabelsUseCurrentName(UiLanguage language)
    {
        var previous = UiText.Language;
        try
        {
            UiText.SetLanguage(language);
            foreach (var label in new[] { UiText.ProductName, UiText.OpenCodexMeter, UiText.SettingsTitle, UiText.ToastSyncTitle })
                Assert.Contains("CodexMeter", label, StringComparison.Ordinal);
        }
        finally { UiText.SetLanguage(previous); }
    }

    [Fact]
    public void RenamePreservesExistingStorageAndInstallationKeys()
    {
        var oldRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProMeter");
        Assert.Equal(Path.Combine(oldRoot, "settings.json"), AppPaths.Settings);
        Assert.Equal(Path.Combine(oldRoot, "codex-snapshot.json"), AppPaths.CodexSnapshot);
        Assert.Equal(Path.Combine(oldRoot, "prometer.db"), AppPaths.Database);
        Assert.Equal(Path.Combine(oldRoot, "com.prometer.bridge.json"), AppPaths.CompanionHostManifest);
        Assert.Equal("com.prometer.bridge", LegacyInstallation.NativeHostName);
        Assert.Equal("ProMeter", LegacyInstallation.StartupValueName);
        Assert.Equal("prometer.exe", LegacyInstallation.ExecutableFileName);
        Assert.Equal(@"Local\ProMeter.SingleInstance", LegacyInstallation.SingleInstanceMutexName);
        Assert.Equal("ProMeterCompanion", LegacyInstallation.CompanionPipeName);
    }
}
