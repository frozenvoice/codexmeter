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
        Assert.Equal("cyclearc", client["name"]!.GetValue<string>());
        Assert.Equal("CycleArc", client["title"]!.GetValue<string>());
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
            foreach (var label in new[] { UiText.ProductName, UiText.OpenApp, UiText.SettingsTitle,
                UiText.ToastSyncTitle, UiText.WidgetTitle, UiText.AboutTitle })
                Assert.Contains("CycleArc", label, StringComparison.Ordinal);
            Assert.Equal("Codex", UiText.CodexProviderName);
            var tooltip = CodexMeterPresentation.Tooltip(CodexQuotaSnapshot.Empty(CodexQuotaStatus.SignedOut));
            Assert.StartsWith("CycleArc · Codex", tooltip, StringComparison.Ordinal);
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
        Assert.Equal("CodexMeter", LegacyInstallation.CodexMeterStartupValueName);
        Assert.Equal("CodexMeter.exe", LegacyInstallation.CodexMeterExecutableFileName);
        Assert.Equal(@"Local\ProMeter.SingleInstance", LegacyInstallation.SingleInstanceMutexName);
        Assert.Equal("ProMeterCompanion", LegacyInstallation.CompanionPipeName);
    }

    [Theory]
    [InlineData("CodexMeter.exe")]
    [InlineData("prometer.exe")]
    public void StartupRenameOnlyRemovesEntriesOwnedByThisInstallation(string previousFile)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CycleArc-install-test");
        var current = Path.Combine(directory, "CycleArc.exe");
        var previous = Path.Combine(directory, previousFile);
        Assert.True(LegacyInstallation.OwnsStartupCommand("\"" + previous + "\"", current, previousFile));
        Assert.True(LegacyInstallation.OwnsStartupCommand("\"" + current + "\"", current, previousFile));
        Assert.False(LegacyInstallation.OwnsStartupCommand("\"" + Path.Combine(directory, "other", previousFile)
            + "\"", current, previousFile));
        Assert.False(LegacyInstallation.OwnsStartupCommand("\"" + previous + "\" --unrecognized", current, previousFile));
        Assert.False(LegacyInstallation.OwnsStartupCommand(null, current, previousFile));
        Assert.False(LegacyInstallation.OwnsStartupCommand("\"" + previous + "\"", null, previousFile));
    }
}
