namespace ProMeter.Services;

public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? AppPaths.Settings;
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return AppSettings.CreateNewInstall();
            }

            try
            {
                var json = File.ReadAllText(_path);
                return SettingsMigration.FromJson(json);
            }
            catch
            {
                return AppSettings.CreateDefaults();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_path, json);
        }
    }
}
