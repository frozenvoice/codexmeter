namespace ProMeter.Services;

public interface IWindowsStartup
{
    void Apply(bool enabled);
}

public static class StartupConsent
{
    public static bool MayWriteStartup(AppSettings settings) => settings.FirstRunCompleted;

    public static void ApplyIfPermitted(IWindowsStartup startup, AppSettings settings)
    {
        if (!MayWriteStartup(settings))
        {
            return;
        }

        startup.Apply(settings.StartWithWindows);
    }
}
