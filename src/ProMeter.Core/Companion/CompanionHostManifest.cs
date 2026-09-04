using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ProMeter.Companion;

public sealed record CompanionHostManifestResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? HostPath { get; init; }
    public string? ManifestJson { get; init; }
    public IReadOnlyList<string> AllowedOrigins { get; init; } = [];
}

public static class CompanionHostManifest
{
    public static readonly Regex ExtensionIdPattern = new("^[a-p]{32}$", RegexOptions.CultureInvariant);

    public static bool IsExtensionId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ExtensionIdPattern.IsMatch(value.Trim());

    public static CompanionHostManifestResult TryCreate(string? companionHostPath, string? chromeExtensionId, string? edgeExtensionId)
    {
        if (string.IsNullOrWhiteSpace(companionHostPath)
            || !File.Exists(companionHostPath)
            || !string.Equals(Path.GetFileName(companionHostPath), "prometer-companion-host.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new CompanionHostManifestResult { Error = "prometer-companion-host.exe is missing" };
        }

        var origins = new List<string>();
        if (!string.IsNullOrWhiteSpace(chromeExtensionId))
        {
            if (!IsExtensionId(chromeExtensionId))
            {
                return new CompanionHostManifestResult { Error = "Chrome extension ID is malformed" };
            }

            origins.Add("chrome-extension://" + chromeExtensionId.Trim() + "/");
        }

        if (!string.IsNullOrWhiteSpace(edgeExtensionId))
        {
            if (!IsExtensionId(edgeExtensionId))
            {
                return new CompanionHostManifestResult { Error = "Edge extension ID is malformed" };
            }

            var origin = "chrome-extension://" + edgeExtensionId.Trim() + "/";
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            {
                origins.Add(origin);
            }
        }

        if (origins.Count == 0)
        {
            return new CompanionHostManifestResult { Error = "a Chrome or Edge extension ID is required" };
        }

        var allowed = new JsonArray();
        foreach (var origin in origins)
        {
            allowed.Add(origin);
        }

        var manifest = new JsonObject
        {
            ["name"] = CompanionBridgeProtocol.NativeHostName,
            ["description"] = "ProMeter ChatGPT companion",
            ["path"] = Path.GetFullPath(companionHostPath),
            ["type"] = "stdio",
            ["allowed_origins"] = allowed
        };

        return new CompanionHostManifestResult
        {
            Ok = true,
            HostPath = Path.GetFullPath(companionHostPath),
            ManifestJson = manifest.ToJsonString(),
            AllowedOrigins = origins
        };
    }

    public static bool HasOnlyApprovedOrigins(string manifestJson, IReadOnlyCollection<string> expected)
    {
        var node = JsonNode.Parse(manifestJson) as JsonObject;
        if (node?["allowed_origins"] is not JsonArray origins || origins.Count == 0)
        {
            return false;
        }

        var actual = origins.Select(item => item?.GetValue<string>() ?? "").ToList();
        return actual.Count == expected.Count
               && actual.All(origin => expected.Contains(origin, StringComparer.OrdinalIgnoreCase));
    }
}
