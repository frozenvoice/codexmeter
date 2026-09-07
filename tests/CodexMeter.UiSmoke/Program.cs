using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexMeter.Codex;
using CodexMeter.Models;
using CodexMeter.Services;
using CodexMeter.UI;

namespace CodexMeter.UiSmoke;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Load the production App/XAML without Run: no startup, account access,
        // settings writes, tray registration or background refresh occurs.
        var app = new App();
        try
        {
            app.InitializeComponent();
            var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException("App.ApplyTheme");
            var count = 0;
            foreach (var language in Enum.GetValues<UiLanguage>())
            foreach (var theme in Enum.GetValues<AppTheme>())
            {
                UiText.SetLanguage(language);
                applyTheme.Invoke(null, [theme]);
                var flyout = new FlyoutWindow();
                var widget = new FloatingWidget();
                var now = DateTimeOffset.Now;
                var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now.AddMinutes(-3), now,
                    null, null, 3, [new CodexQuotaWindow("codex", 28, 10080, now.AddDays(7), CodexWindowKind.Weekly)], null);
                flyout.Bind(snapshot);
                widget.Bind(snapshot);
                Window[] windows = [flyout, widget,
                    new SettingsWindow(AppSettings.CreateDefaults()), new AboutWindow("1.0.0", "synthetic")];
                foreach (var window in windows)
                {
                    try
                    {
                        var content = (FrameworkElement)window.Content;
                        content.Measure(new Size(double.IsFinite(window.Width) ? window.Width : 900, 900));
                        content.Arrange(new Rect(new Point(), content.DesiredSize));
                        content.UpdateLayout();
                        if (window is FlyoutWindow)
                        {
                            var rows = (System.Windows.Controls.ItemsControl)window.FindName("CodexRows");
                            var last = (System.Windows.Controls.Border)rows.Items[rows.Items.Count - 1];
                            var grid = (System.Windows.Controls.Grid)last.Child;
                            var label = (FrameworkElement)grid.Children[0];
                            var value = (FrameworkElement)grid.Children[1];
                            var labelRight = label.TranslatePoint(new Point(label.ActualWidth, 0), grid).X;
                            var valueLeft = value.TranslatePoint(new Point(), grid).X;
                            if (valueLeft < labelRight || valueLeft + value.ActualWidth > grid.ActualWidth + 1)
                                throw new InvalidOperationException("Last checked text overlaps or overflows.");
                        }
                        if (content.ActualWidth <= 0 || content.ActualHeight <= 0)
                            throw new InvalidOperationException($"Empty layout: {window.GetType().Name}");
                        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth),
                            (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(content);
                        count++;
                    }
                    finally { window.Close(); }
                }
            }
            Console.WriteLine($"PASS: {count} production WPF resource/layout renders across languages and themes.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally { app.Shutdown(); }
    }
}
