using Microsoft.Win32;

namespace CodexMeter.Services;

public sealed class WindowsStartupService : IWindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CycleArc";

    public void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
        {
            return;
        }

        // Preserve startup consent and touch only previous entries owned by this installation.
        foreach (var (name, file) in new[]
        {
            (LegacyInstallation.StartupValueName, LegacyInstallation.ExecutableFileName),
            (LegacyInstallation.CodexMeterStartupValueName, LegacyInstallation.CodexMeterExecutableFileName)
        })
        {
            if (LegacyInstallation.OwnsStartupCommand(key.GetValue(name) as string, Environment.ProcessPath, file))
                key.DeleteValue(name, throwOnMissingValue: false);
        }

        if (enabled)
        {
            var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
            key.SetValue(ValueName, "\"" + exe + "\"");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string;
    }
}
