using System.Text.Json;

namespace CodexMeter.Codex;

public sealed record CodexAccountConfiguration(int Version, string SelectedId, IReadOnlyList<CodexAccountProfile> Profiles)
{
    public IReadOnlyList<string> IgnoredHomes { get; init; } = [];
}

public sealed class CodexAccountStore
{
    public const string LegacyProfileId = "default";
    private readonly string _root;
    private readonly object _gate = new();
    private string RegistryPath => Path.Combine(_root, "codex-accounts.json");
    public bool RecoveredFromBackup { get; private set; }

    public CodexAccountStore(string? root = null) => _root = Path.GetFullPath(root ?? Services.AppPaths.Root);

    public CodexAccountConfiguration LoadOrMigrate(string defaultHome)
    {
        lock (_gate)
        {
            RecoveredFromBackup = false;
            if (TryRead(RegistryPath, out var state)) return state!;
            if (TryRead(RegistryPath + ".bak", out state))
            {
                RecoveredFromBackup = true;
                return state!;
            }
            // Do not overwrite an unsupported/damaged registry with a new account collection.
            if (File.Exists(RegistryPath) || File.Exists(RegistryPath + ".bak"))
                throw new InvalidDataException("Account registry could not be loaded.");
            var home = CodexHomeDiscovery.Normalize(defaultHome) ?? throw new ArgumentException("Invalid Codex home.");
            state = new(1, LegacyProfileId, [new(LegacyProfileId, home, "")]);
            Save(state);
            return state;
        }
    }

    public CodexAccountProfile NewManaged(string label)
    {
        var id = Guid.NewGuid().ToString("N");
        return new(id, Path.Combine(_root, "accounts", id, "codex-home"), CleanLabel(label), true);
    }

    public string SnapshotPath(CodexAccountProfile profile) => profile.Id == LegacyProfileId
        ? Path.Combine(_root, "codex-snapshot.json")
        : Path.Combine(_root, "accounts", RequireId(profile.Id), "quota.json");

    public void Save(CodexAccountConfiguration state)
    {
        if (!IsValid(state)) throw new InvalidDataException("Invalid account registry.");
        lock (_gate)
        {
            Directory.CreateDirectory(_root);
            var temp = RegistryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, state);
                    stream.Flush(true);
                }
                if (TryRead(RegistryPath, out _)) File.Replace(temp, RegistryPath, RegistryPath + ".bak", true);
                else File.Move(temp, RegistryPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    public static string CleanLabel(string? label) => new((label ?? "").Trim().Where(c => !char.IsControl(c)).Take(80).ToArray());
    private static string RequireId(string id) => id == LegacyProfileId || Guid.TryParseExact(id, "N", out _)
        ? id : throw new InvalidDataException("Invalid local profile identifier.");

    private bool TryRead(string path, out CodexAccountConfiguration? state)
    {
        state = null;
        try
        {
            state = JsonSerializer.Deserialize<CodexAccountConfiguration>(File.ReadAllText(path));
            return state is not null && IsValid(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return false; }
    }

    private bool IsValid(CodexAccountConfiguration state)
    {
        if (state.Version != 1 || state.Profiles is null || state.Profiles.Any(p => p is null)
            || state.IgnoredHomes is null || state.IgnoredHomes.Any(p => CodexHomeDiscovery.Normalize(p) is null)) return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var homes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in state.Profiles)
        {
            if (profile.Id is null || (profile.Id != LegacyProfileId && !Guid.TryParseExact(profile.Id, "N", out _))
                || !ids.Add(profile.Id) || CodexHomeDiscovery.Normalize(profile.HomePath) is not { } home
                || !homes.Add(home) || profile.Label != CleanLabel(profile.Label)) return false;
            if (profile.IsManaged && (profile.Id == LegacyProfileId || !string.Equals(home,
                    Path.Combine(_root, "accounts", profile.Id, "codex-home"), StringComparison.OrdinalIgnoreCase))) return false;
        }
        return state.Profiles.Count == 0 ? state.SelectedId == "" : ids.Contains(state.SelectedId);
    }
}
