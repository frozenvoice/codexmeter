using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexMeter.Codex;
using CodexMeter.Models;
using CodexMeter.Services;
using CodexMeter.UI;

namespace CodexMeter.UiSmoke;

internal static class DocumentationScreenshots
{
    // Production views, synthetic profiles/quota metadata, no account or local settings access.
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
        ExportAccounts(directory, now, applyTheme);
        Console.WriteLine("Exported 12 production WPF views with synthetic profiles/quota data; no user account access.");
    }

    private static void ExportAccounts(string directory, DateTimeOffset now, MethodInfo applyTheme)
    {
        foreach (var language in new[] { UiLanguage.English, UiLanguage.Korean })
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var accounts = SampleAccounts(now);
            var selected = accounts[1].Profile.Id;
            var suffix = $"{(language == UiLanguage.English ? "en" : "ko")}-{theme.ToString().ToLowerInvariant()}";
            var flyout = new FlyoutWindow();
            var manager = new AccountsWindow();
            try
            {
                flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
                flyout.BindAccounts(accounts, selected, false);
                Save(flyout, Path.Combine(directory, $"accounts-overview-{suffix}.png"), 440, null);
                manager.Bind(accounts, selected);
                Save(manager, Path.Combine(directory, $"accounts-manage-{suffix}.png"), 700, 800,
                    () => ((ScrollViewer)manager.FindName("AccountsScroll")).ScrollToBottom());
            }
            finally { flyout.Close(); manager.Close(); }
        }
    }

    private static CodexAccountView[] SampleAccounts(DateTimeOffset now)
    {
        // These paths are display-only fixture values. Never create or inspect Codex homes here.
        var names = new[] { UiText.T("Personal", "개인용"), UiText.T("Work", "업무용"), UiText.T("Research", "실험용") };
        var slugs = new[] { "personal", "work", "research" };
        var used = new[] { 18d, 64d, 91d };
        return Enumerable.Range(0, names.Length).Select(i => new CodexAccountView(
            new CodexAccountProfile(i == 0 ? "default" : i.ToString("D32"),
                @"C:\CodexMeter-Samples\" + slugs[i], names[i], i > 0),
            new CodexQuotaSnapshot(i == 2 ? CodexQuotaStatus.Stale : CodexQuotaStatus.Available, "pro",
                now.AddMinutes(i == 2 ? -180 : -3), now.AddMinutes(-3), null, null, 2,
                [new("codex", used[i], 10080, now.AddDays(2 + i).AddHours(6), CodexWindowKind.Weekly)], null,
                [now.AddDays(28), now.AddDays(54)]), $"{slugs[i]}@example.invalid")).ToArray();
    }

    private static void Save(Window window, string path, double width, double? height, Action? arranged = null)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(width, height ?? double.PositiveInfinity));
        var size = new Size(width, height ?? content.DesiredSize.Height);
        content.Arrange(new Rect(new Point(), size));
        content.UpdateLayout();
        arranged?.Invoke();
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
