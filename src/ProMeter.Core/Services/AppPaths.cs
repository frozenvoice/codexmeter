namespace ProMeter.Services;

public static class AppPaths
{
    public static string Root
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProMeter");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string Database => Path.Combine(Root, "prometer.db");
    public static string Settings => Path.Combine(Root, "settings.json");
    public static string Logs => Directory.CreateDirectory(Path.Combine(Root, "logs")).FullName;
    public static string WebViewProfile => Directory.CreateDirectory(Path.Combine(Root, "webview")).FullName;
    public static string CompanionPairing => Path.Combine(Root, "companion-pairing.json");
    public static string CompanionHostManifest => Path.Combine(Root, "com.prometer.bridge.json");
    public static string CodexSnapshot => Path.Combine(Root, "codex-snapshot.json");
}
