namespace CycleArc.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();
    public bool RecoveredFromBackup { get; private set; }
    public string BackupPath => _path + ".bak";

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.Settings;
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            RecoveredFromBackup = false;
            if (TryRead(_path, out var settings)) return settings!;
            if (TryRead(BackupPath, out settings))
            {
                RecoveredFromBackup = true;
                return settings!;
            }
            return File.Exists(_path) || File.Exists(BackupPath)
                ? AppSettings.CreateDefaults() : AppSettings.CreateNewInstall();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, settings, new JsonSerializerOptions { WriteIndented = true });
                    stream.Flush(flushToDisk: true);
                }
                if (TryRead(_path, out _))
                    File.Replace(temporary, _path, BackupPath, ignoreMetadataErrors: true);
                else
                    // An unreadable/corrupt primary must never replace a known-good backup.
                    File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static bool TryRead(string path, out AppSettings? settings)
    {
        settings = null;
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            settings = SettingsMigration.FromJson(json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
        }
    }
}
