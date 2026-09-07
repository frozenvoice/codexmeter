using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProMeter.Companion;

/// <summary>
/// Reads the ProMeter Browser Companion extension's own manifest.json to recover
/// its deterministic extension ID, so ProMeter never has to be told the ID by
/// the user or scrape browser profile data to discover it.
/// </summary>
public static class CompanionExtensionManifest
{
    public static bool TryReadBuiltInExtensionId(string manifestPath, out string? extensionId)
    {
        extensionId = null;
        if (!TryReadPublicKey(manifestPath, out var key) || key is null)
        {
            return false;
        }

        return ChromiumExtensionId.TryCompute(key, out extensionId) && extensionId is not null;
    }

    public static bool TryReadPublicKey(string manifestPath, out string? base64PublicKey)
    {
        base64PublicKey = null;
        if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var node = JsonNode.Parse(json) as JsonObject;
            var key = node?["key"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            base64PublicKey = key.Trim();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
