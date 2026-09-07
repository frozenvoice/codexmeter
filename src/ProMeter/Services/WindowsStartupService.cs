using Microsoft.Win32;

namespace ProMeter.Services;

public sealed class WindowsStartupService : IWindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodexMeter";

    public void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
        {
            return;
        }

        // Remove the previous product's entry only when it points into this installation.
        if (key.GetValue("ProMeter") is string legacy && Environment.ProcessPath is string current)
        {
            var previousExe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(current)!, "prometer.exe");
            if (string.Equals(legacy, "\"" + previousExe + "\"", StringComparison.OrdinalIgnoreCase)
                || string.Equals(legacy, "\"" + current + "\"", StringComparison.OrdinalIgnoreCase))
                key.DeleteValue("ProMeter", throwOnMissingValue: false);
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
