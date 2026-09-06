using System.IO;
using Microsoft.Win32;

namespace ProMeter.Companion;

public static class CompanionRegistration
{
    public const string ChromeNativeHosts = @"Software\Google\Chrome\NativeMessagingHosts\" + CompanionBridgeProtocol.NativeHostName;
    public const string EdgeNativeHosts = @"Software\Microsoft\Edge\NativeMessagingHosts\" + CompanionBridgeProtocol.NativeHostName;

    public static CompanionHostManifestResult EnsureCurrent(string trayExePath, string? chromeExtensionId, string? edgeExtensionId)
    {
        if (string.IsNullOrWhiteSpace(chromeExtensionId) && string.IsNullOrWhiteSpace(edgeExtensionId))
        {
            return new CompanionHostManifestResult { Ok = true };
        }

        return Register(trayExePath, chromeExtensionId, edgeExtensionId);
    }

    public static CompanionHostManifestResult Register(string trayExePath, string? chromeExtensionId, string? edgeExtensionId)
    {
        var releaseHost = ResolveHostPath(trayExePath);
        if (releaseHost is null)
        {
            return new CompanionHostManifestResult { Error = "prometer-companion-host.exe is missing" };
        }

        string stagedHost;
        try
        {
            stagedHost = CompanionHostStager.EnsureStaged(releaseHost);
        }
        catch (Exception ex)
        {
            return new CompanionHostManifestResult { Error = "companion host staging failed: " + ex.GetType().Name };
        }

        var created = CompanionHostManifest.TryCreate(stagedHost, chromeExtensionId, edgeExtensionId);
        if (!created.Ok || created.ManifestJson is null || created.HostPath is null)
        {
            return created;
        }

        File.WriteAllText(AppPaths.CompanionHostManifest, created.ManifestJson);
        if (!CompanionHostManifest.HasOnlyApprovedOrigins(File.ReadAllText(AppPaths.CompanionHostManifest), created.AllowedOrigins))
        {
            return new CompanionHostManifestResult { Error = "native-host manifest origins were not approved" };
        }

        if (!string.IsNullOrWhiteSpace(chromeExtensionId))
        {
            SetHost(Registry.CurrentUser, ChromeNativeHosts, AppPaths.CompanionHostManifest);
        }

        if (!string.IsNullOrWhiteSpace(edgeExtensionId))
        {
            SetHost(Registry.CurrentUser, EdgeNativeHosts, AppPaths.CompanionHostManifest);
        }

        if (string.IsNullOrWhiteSpace(chromeExtensionId) && string.IsNullOrWhiteSpace(edgeExtensionId))
        {
            return new CompanionHostManifestResult { Error = "a Chrome or Edge extension ID is required" };
        }

        return created;
    }

    public static string WhaleInstructions =>
        "Whale is a Chromium browser, but ProMeter does not invent a registry location for it. Use Chrome or Edge official Native Messaging registration, or follow Whale's own extension/native-host documentation.";

    public static string? ResolveHostPath(string trayExePath)
    {
        var directory = Path.GetDirectoryName(trayExePath.Trim('"')) ?? AppContext.BaseDirectory;
        var companion = Path.Combine(directory, "prometer-companion-host.exe");
        return File.Exists(companion) ? Path.GetFullPath(companion) : null;
    }

    private static void SetHost(RegistryKey root, string keyPath, string manifestPath)
    {
        using var key = root.CreateSubKey(keyPath);
        key?.SetValue(null, manifestPath);
    }
}
