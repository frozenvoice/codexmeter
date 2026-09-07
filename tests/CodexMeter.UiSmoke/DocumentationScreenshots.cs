using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexMeter.Codex;
using CodexMeter.Models;
using CodexMeter.Services;
using CodexMeter.UI;

namespace CodexMeter.UiSmoke;

internal static class DocumentationScreenshots
{
    // Production views, synthetic quota metadata, no account or local settings access.
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        UiText.SetLanguage(UiLanguage.English);
        var now = DateTimeOffset.Now;
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now.AddMinutes(-3),
            now.AddMinutes(-3), null, null, 3,
            [new("codex", 28, 10080, now.AddDays(4).AddHours(6), CodexWindowKind.Weekly)], null,
            [now.AddDays(30), now.AddDays(30), now.AddDays(60)]);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            applyTheme.Invoke(null, [theme]);
            var flyout = new FlyoutWindow();
            try
            {
                flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
                flyout.Bind(snapshot);
                Save(flyout, Path.Combine(directory, $"overview-{theme.ToString().ToLowerInvariant()}.png"), 440, null);
            }
            finally { flyout.Close(); }
        }
        applyTheme.Invoke(null, [AppTheme.Dark]);
        var settings = new SettingsWindow(new AppSettings { UiLanguage = UiLanguage.English, Theme = AppTheme.Dark });
        try { Save(settings, Path.Combine(directory, "settings.png"), 640, 590); }
        finally { settings.Close(); }
        var widget = new FloatingWidget();
        try
        {
            widget.Bind(snapshot);
            Save(widget, Path.Combine(directory, "widget.png"), 220, null);
        }
        finally { widget.Close(); }
        Console.WriteLine("Exported 4 English production WPF views with synthetic quota data.");
    }

    private static void Save(Window window, string path, double width, double? height)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? content.DesiredSize.Height);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size.Width * 2),
            (int)Math.Ceiling(size.Height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
