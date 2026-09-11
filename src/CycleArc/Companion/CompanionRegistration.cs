using System.IO;
using Microsoft.Win32;

namespace CycleArc.Companion;

public static class CompanionRegistration
{
    public const string ChromeNativeHosts = @"Software\Google\Chrome\NativeMessagingHosts\" + CompanionBridgeProtocol.NativeHostName;
    public const string EdgeNativeHosts = @"Software\Microsoft\Edge\NativeMessagingHosts\" + CompanionBridgeProtocol.NativeHostName;

    public static CompanionHostManifestResult EnsureCurrent(
        string trayExePath,
        string? chromeExtensionId,
        string? edgeExtensionId,
        string? builtInExtensionId = null)
    {
        if (string.IsNullOrWhiteSpace(chromeExtensionId)
            && string.IsNullOrWhiteSpace(edgeExtensionId)
            && string.IsNullOrWhiteSpace(builtInExtensionId))
        {
            return new CompanionHostManifestResult { Ok = true };
        }

        return Register(trayExePath, chromeExtensionId, edgeExtensionId, builtInExtensionId);
    }

    public static CompanionHostManifestResult Register(
        string trayExePath,
        string? chromeExtensionId,
        string? edgeExtensionId,
        string? builtInExtensionId = null)
    {
        var releaseHost = ResolveHostPath(trayExePath);
        if (releaseHost is null)
        {
            return new CompanionHostManifestResult { Error = "cyclearc-companion-host.exe is missing" };
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

        var created = CompanionHostManifest.TryCreate(stagedHost, chromeExtensionId, edgeExtensionId, builtInExtensionId);
        if (!created.Ok || created.ManifestJson is null || created.HostPath is null)
        {
            return created;
        }

        File.WriteAllText(AppPaths.CompanionHostManifest, created.ManifestJson);
        if (!CompanionHostManifest.HasOnlyApprovedOrigins(File.ReadAllText(AppPaths.CompanionHostManifest), created.AllowedOrigins))
        {
            return new CompanionHostManifestResult { Error = "native-host manifest origins were not approved" };
        }

        // The deterministic built-in ID is browser-agnostic, so its presence alone is
        // enough to register both browsers. A manually configured ID still only implies
        // registration for that specific browser, preserving prior single-browser behavior.
        if (!string.IsNullOrWhiteSpace(chromeExtensionId) || !string.IsNullOrWhiteSpace(builtInExtensionId))
        {
            SetHost(Registry.CurrentUser, ChromeNativeHosts, AppPaths.CompanionHostManifest);
        }

        if (!string.IsNullOrWhiteSpace(edgeExtensionId) || !string.IsNullOrWhiteSpace(builtInExtensionId))
        {
            SetHost(Registry.CurrentUser, EdgeNativeHosts, AppPaths.CompanionHostManifest);
        }

        return created;
    }

    public static string WhaleInstructions =>
        "Whale is a Chromium browser, but CycleArc does not invent a registry location for it. Use Chrome or Edge official Native Messaging registration, or follow Whale's own extension/native-host documentation.";

    public static string? ResolveHostPath(string trayExePath)
    {
        var directory = Path.GetDirectoryName(trayExePath.Trim('"')) ?? AppContext.BaseDirectory;
        var companion = Path.Combine(directory, "cyclearc-companion-host.exe");
        return File.Exists(companion) ? Path.GetFullPath(companion) : null;
    }

    private static void SetHost(RegistryKey root, string keyPath, string manifestPath)
    {
        using var key = root.CreateSubKey(keyPath);
        key?.SetValue(null, manifestPath);
    }
}
