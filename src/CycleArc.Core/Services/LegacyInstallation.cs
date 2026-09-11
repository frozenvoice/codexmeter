namespace CycleArc.Services;

/// <summary>
/// Persisted identifiers from earlier installations. These are compatibility keys,
/// not product branding; changing them would orphan data or miss old registrations.
/// </summary>
public static class LegacyInstallation
{
    public const string DataDirectoryName = "ProMeter";
    public const string DatabaseFileName = "prometer.db";
    public const string NativeHostName = "com.prometer.bridge";
    public const string NativeHostManifestFileName = "com.prometer.bridge.json";
    public const string StartupValueName = "ProMeter";
    public const string ExecutableFileName = "prometer.exe";
    public const string PreviousStartupValueName = "CodexMeter";
    public const string PreviousExecutableFileName = "CodexMeter.exe";
    public const string SingleInstanceMutexName = @"Local\ProMeter.SingleInstance";
    public const string CompanionPipeName = "ProMeterCompanion";
    public const string DatabaseBackupFormat = "prometer-pre-reconstruction-v{0}-to-v{1}-{2}.db";
    public const string DatabaseBackupPattern = "prometer-pre-reconstruction-*.db";

    public static bool OwnsStartupCommand(string? command, string? currentExecutable, string previousFileName)
    {
        if (command is null || string.IsNullOrWhiteSpace(currentExecutable)
            || !Path.IsPathFullyQualified(currentExecutable)) return false;
        var previousExecutable = Path.Combine(Path.GetDirectoryName(currentExecutable)!, previousFileName);
        return string.Equals(command, "\"" + previousExecutable + "\"", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "\"" + currentExecutable + "\"", StringComparison.OrdinalIgnoreCase);
    }
}
