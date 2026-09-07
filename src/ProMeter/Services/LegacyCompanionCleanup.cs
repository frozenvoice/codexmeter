using System.IO;
using Microsoft.Win32;

namespace ProMeter.Services;

public static class LegacyCompanionCleanup
{
    public static void Unregister(Action<string> warning)
    {
        var expected = Path.GetFullPath(AppPaths.CompanionHostManifest);
        foreach (var browser in new[] { @"Google\Chrome", @"Microsoft\Edge", @"Naver\Naver Whale" })
        {
            var parentPath = @"Software\" + browser + @"\NativeMessagingHosts";
            try
            {
                using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
                if (parent is null) continue;
                using var registration = parent.OpenSubKey("com.prometer.bridge");
                if (registration?.GetValue(null) is not string manifest
                    || !Path.GetFullPath(manifest).Equals(expected, StringComparison.OrdinalIgnoreCase)) continue;
                registration.Close();
                parent.DeleteSubKey("com.prometer.bridge", throwOnMissingSubKey: false);
            }
            catch (Exception ex) { warning("legacy companion unregister: " + ex.GetType().Name); }
        }
    }
}
