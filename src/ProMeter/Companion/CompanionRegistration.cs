using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Companion;

public static class CompanionRegistration
{
    public const string ChromeNativeHosts = @"Software\Google\Chrome\NativeMessagingHosts\" + CompanionBridgeProtocol.NativeHostName;
    public const string EdgeNativeHosts = @"Software\Microsoft\Edge\NativeMessagingHosts\" + CompanionBridgeProtocol.NativeHostName;

    public static string WriteHostManifest(string exePath, string? extensionId)
    {
        var host = ResolveHostPath(exePath);
        var origins = new JsonArray();
        if (!string.IsNullOrWhiteSpace(extensionId))
        {
            origins.Add("chrome-extension://" + extensionId.Trim() + "/");
        }

        var manifest = new JsonObject
        {
            ["name"] = CompanionBridgeProtocol.NativeHostName,
            ["description"] = "ProMeter ChatGPT companion",
            ["path"] = host,
            ["type"] = "stdio",
            ["allowed_origins"] = origins
        };
        File.WriteAllText(AppPaths.CompanionHostManifest, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return AppPaths.CompanionHostManifest;
    }

    public static void RegisterOfficialChromiumHosts(string manifestPath)
    {
        SetHost(Registry.CurrentUser, ChromeNativeHosts, manifestPath);
        SetHost(Registry.CurrentUser, EdgeNativeHosts, manifestPath);
    }

    public static string WhaleInstructions =>
        "Whale is a Chromium browser, but ProMeter does not invent a registry location for it. Use Chrome or Edge official Native Messaging registration, or follow Whale's own extension/native-host documentation.";

    public static string ResolveHostPath(string trayExePath)
    {
        var directory = Path.GetDirectoryName(trayExePath.Trim('"')) ?? AppContext.BaseDirectory;
        var companion = Path.Combine(directory, "prometer-companion-host.exe");
        return File.Exists(companion) ? companion : trayExePath.Trim('"');
    }

    private static void SetHost(RegistryKey root, string keyPath, string manifestPath)
    {
        using var key = root.CreateSubKey(keyPath);
        key?.SetValue(null, manifestPath);
    }
}
