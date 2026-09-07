namespace CodexMeter.Services;

public sealed class CompanionPairingState
{
    public string Token { get; set; } = "";
    public string? ExtensionId { get; set; }
    public string? ChromeExtensionId { get; set; }
    public string? EdgeExtensionId { get; set; }
}

public static class CompanionPairingStore
{
    public static CompanionPairingState LoadOrCreate(string? path = null)
    {
        var file = path ?? AppPaths.CompanionPairing;
        try
        {
            if (File.Exists(file))
            {
                var loaded = JsonSerializer.Deserialize<CompanionPairingState>(File.ReadAllText(file), ChatGptJson.Options);
                if (loaded is { Token.Length: > 16 })
                {
                    return loaded;
                }
            }
        }
        catch
        {
        }

        var created = new CompanionPairingState { Token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N") };
        Save(created, file);
        return created;
    }

    public static void Save(CompanionPairingState state, string? path = null)
    {
        var file = path ?? AppPaths.CompanionPairing;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static bool TokensEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && string.Equals(left, right, StringComparison.Ordinal);
}
