using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using CodexMeter.Codex;
using CodexMeter.Models;
using CodexMeter.Services;
using CodexMeter.UI;

namespace CodexMeter.UiSmoke;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Load the production App/XAML without Run: no startup, account access,
        // settings writes, tray registration or background refresh occurs.
        var app = new App();
        try
        {
            app.InitializeComponent();
            if (args is ["--screenshots", var directory])
            {
                DocumentationScreenshots.Export(directory);
                return 0;
            }
            CheckEnvironmentCallbacks(app);
            CheckWidgetRecovery();
            CheckPositionReset();
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
                CheckZoomShortcuts(flyout);
                widget.Bind(snapshot);
                var notice = (System.Windows.Controls.TextBlock)widget.FindName("HistoryValue");
                foreach (var status in Enum.GetValues<CodexQuotaStatus>())
                {
                    widget.Bind(snapshot with { Status = status });
                    var attention = status is not (CodexQuotaStatus.Available or CodexQuotaStatus.Refreshing);
                    if (notice.Visibility != (attention ? Visibility.Visible : Visibility.Collapsed)
                        || string.IsNullOrEmpty(notice.Text) == attention)
                        throw new InvalidOperationException($"Incorrect widget notice for {status}.");
                }
                widget.Bind(snapshot); // Recovery must remove the old failure text and its space.
                if (notice.Visibility != Visibility.Collapsed || notice.Text.Length != 0)
                    throw new InvalidOperationException("Widget notice remains after recovery.");
                Window[] windows = [flyout, widget,
                    new SettingsWindow(AppSettings.CreateDefaults()), new AboutWindow("1.0.0", "synthetic")];
                foreach (var window in windows)
                {
                    try
                    {
                        foreach (var zoom in window is FlyoutWindow ? new[] { 80, 100, 150 } : new[] { 100 })
                        {
                            if (window is FlyoutWindow zoomWindow)
                            {
                                zoomWindow.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = zoom });
                                zoomWindow.Bind(snapshot);
                                if (zoomWindow.ZoomPercent != zoom)
                                    throw new InvalidOperationException("Refresh reset the popup zoom.");
                            }
                            var content = (FrameworkElement)window.Content;
                            content.Measure(new Size(double.IsFinite(window.Width) ? window.Width : 900, 900));
                            content.Arrange(new Rect(new Point(), content.DesiredSize));
                            content.UpdateLayout();
                            if (window is FlyoutWindow)
                            {
                                var rows = (System.Windows.Controls.ItemsControl)window.FindName("CodexRows");
                                foreach (System.Windows.Controls.Border row in rows.Items)
                                {
                                    var grid = (System.Windows.Controls.Grid)row.Child;
                                    var label = (FrameworkElement)grid.Children[0];
                                    var value = (FrameworkElement)grid.Children[1];
                                    var labelRight = label.TranslatePoint(new Point(label.ActualWidth, 0), grid).X;
                                    var valueLeft = value.TranslatePoint(new Point(), grid).X;
                                    if (valueLeft < labelRight || valueLeft + value.ActualWidth > grid.ActualWidth + 1)
                                        throw new InvalidOperationException("Quota row text overlaps or overflows.");
                                }
                            }
                            if (content.ActualWidth <= 0 || content.ActualHeight <= 0)
                                throw new InvalidOperationException($"Empty layout: {window.GetType().Name}");
                            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.DesiredSize.Width),
                                (int)Math.Ceiling(content.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(content);
                            count++;
                        }
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

    private static void CheckZoomShortcuts(FlyoutWindow flyout)
    {
        var changes = new List<int>();
        flyout.ZoomChanged += changes.Add;
        flyout.ApplyWindowSettings(new AppSettings { FlyoutZoomPercent = 100 });
        if (changes.Count != 0 || flyout.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.None)
            || flyout.TryHandleZoomShortcut(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Alt)
            || flyout.TryHandleZoomShortcut(Key.A, ModifierKeys.Control))
            throw new InvalidOperationException("Unexpected zoom shortcut handling.");
        foreach (var (key, modifiers, expected) in new[]
        {
            (Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift, 110),
            (Key.Add, ModifierKeys.Control, 120),
            (Key.OemMinus, ModifierKeys.Control, 110),
            (Key.Subtract, ModifierKeys.Control, 100),
            (Key.OemPlus, ModifierKeys.Control, 110),
            (Key.D0, ModifierKeys.Control, 100),
            (Key.NumPad0, ModifierKeys.Control, 100)
        })
        {
            if (!flyout.TryHandleZoomShortcut(key, modifiers) || flyout.ZoomPercent != expected)
                throw new InvalidOperationException($"Zoom shortcut failed: {key}.");
        }
        if (changes.Count != 6) throw new InvalidOperationException("Zoom saves must occur only on changes.");
        flyout.ZoomChanged -= changes.Add;
    }

    private static void CheckPositionReset()
    {
        var settings = new AppSettings { WidgetLeft = 777, WidgetTop = 888 };
        var window = new SettingsWindow(settings);
        try
        {
            var button = (System.Windows.Controls.Button)window.FindName("ResetWidgetPositionButton");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            if (!window.ResetWidgetPositionOnSave || settings.WidgetLeft != 777 || settings.WidgetTop != 888)
                throw new InvalidOperationException("Position reset must remain pending until Save.");
        }
        finally { window.Close(); }
    }

    private static void CheckWidgetRecovery()
    {
        var widget = new FloatingWidget { Left = 9000, Top = 9000 };
        try
        {
            var changes = 0;
            widget.Moved += (left, top) =>
            {
                changes++;
                if (left != 40 || top != 40) throw new InvalidOperationException("Incorrect recovered coordinates.");
            };
            widget.RecoverPosition([new ScreenRect(0, 0, 1920, 1040)]);
            widget.RecoverPosition([new ScreenRect(0, 0, 1920, 1040)]);
            if (widget.Left != 40 || widget.Top != 40 || changes != 1)
                throw new InvalidOperationException("Widget recovery must persist one position change.");
        }
        finally { widget.Close(); }
    }

    private static void CheckEnvironmentCallbacks(App app)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckDispatcherCallbacks(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Dispatcher test timed out.");
        if (failure is not null) throw new InvalidOperationException("Dispatcher test failed.", failure);

        var settingsField = typeof(App).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var settings = (AppSettings)settingsField.GetValue(app)!;
        var themeChanged = typeof(App).GetMethod("OnSystemThemeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var theme in Enum.GetValues<AppTheme>())
        {
            settings.Theme = theme;
            var sentinel = new SolidColorBrush(Colors.Magenta);
            app.Resources["TextBrush"] = sentinel;
            themeChanged.Invoke(app, null);
            var replaced = !ReferenceEquals(sentinel, app.Resources["TextBrush"]);
            if (replaced != (theme == AppTheme.System)) throw new InvalidOperationException("System change overrode a fixed theme.");
        }
    }

    private static void CheckDispatcherCallbacks()
    {
        // Pump a separate STA dispatcher so production App startup never runs.
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var owner = Environment.CurrentManagedThreadId;
        var themeCalls = 0;
        var displayCalls = 0;
        void CheckThread()
        {
            if (Environment.CurrentManagedThreadId != owner)
                throw new InvalidOperationException("Desktop event escaped the WPF dispatcher.");
        }
        using var monitor = new DesktopEnvironmentMonitor(dispatcher,
            () => { CheckThread(); themeCalls++; }, () => { CheckThread(); displayCalls++; }, false);
        Task.Run(() => { monitor.NotifyThemeChanged(); monitor.NotifyDisplayChanged(); }).GetAwaiter().GetResult();
        if (themeCalls != 0 || displayCalls != 0) throw new InvalidOperationException("Desktop event ran on a worker.");
        PumpDispatcher(dispatcher);
        if (themeCalls != 1 || displayCalls != 1) throw new InvalidOperationException("Desktop event was lost.");
        monitor.NotifyThemeChanged();
        monitor.NotifyDisplayChanged();
        monitor.Dispose();
        monitor.NotifyThemeChanged();
        PumpDispatcher(dispatcher);
        if (themeCalls != 1 || displayCalls != 1) throw new InvalidOperationException("Disposed callbacks executed.");

        dispatcher.InvokeShutdown();
    }

    private static void PumpDispatcher(System.Windows.Threading.Dispatcher dispatcher)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        dispatcher.BeginInvoke(() => frame.Continue = false, System.Windows.Threading.DispatcherPriority.Background);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

}
