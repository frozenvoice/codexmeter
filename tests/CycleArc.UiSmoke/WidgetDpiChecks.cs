using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CycleArc.Codex;
using CycleArc.Models;
using CycleArc.Services;
using CycleArc.UI;

namespace CycleArc.UiSmoke;

internal static class WidgetDpiChecks
{
    // Synthetic quota only. Neither these renders nor the native windows start the production app.
    public static void Run(string? directory = null)
    {
        if (directory is not null) Directory.CreateDirectory(directory);
        var now = DateTimeOffset.Now;
        var snapshot = new CodexQuotaSnapshot(CodexQuotaStatus.Available, "pro", now, now,
            null, null, 3, [new("codex", 25, 10080, now.AddDays(7), CodexWindowKind.Weekly)], null);
        var applyTheme = typeof(App).GetMethod("ApplyTheme", BindingFlags.Static | BindingFlags.NonPublic)!;
        var count = 0;
        foreach (var language in Enum.GetValues<UiLanguage>())
        foreach (var theme in Enum.GetValues<AppTheme>())
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0 })
        {
            UiText.SetLanguage(language);
            applyTheme.Invoke(null, [theme]);
            var widget = new FloatingWidget();
            try
            {
                // Change the visual's layout DPI, not just the output bitmap resolution.
                var content = (FrameworkElement)widget.Content;
                widget.Bind(snapshot);
                VisualTreeHelper.SetRootDpi(content, new DpiScale(scale, scale));
                foreach (var status in new[] { CodexQuotaStatus.Available, CodexQuotaStatus.Stale,
                             CodexQuotaStatus.Available })
                {
                    widget.Bind(snapshot with { Status = status });
                    content.InvalidateMeasure();
                    content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var desired = content.DesiredSize;
                    // Native window constraints can allocate more height than content requests.
                    // Reproduce this even on CI hosts with different window metrics.
                    foreach (var extraHeight in new[] { 0.0, 6.0 })
                    {
                        content.Arrange(new Rect(0, 0, desired.Width, desired.Height + extraHeight));
                        content.UpdateLayout();
                        AssertCentered(widget, $"{language}/{theme}/{scale}/{status}/extra={extraHeight}");
                        var bitmap = Render(content, scale);
                        if (directory is not null && status == CodexQuotaStatus.Available)
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var stream = File.Create(Path.Combine(directory,
                                $"{language}-{theme}-{scale * 100:0}-{extraHeight:0}.png"));
                            encoder.Save(stream);
                        }
                        count++;
                    }
                }
            }
            finally { widget.Close(); }
        }

        UiText.SetLanguage(UiLanguage.Korean);
        applyTheme.Invoke(null, [AppTheme.Dark]);
        var screens = System.Windows.Forms.Screen.AllScreens;
        foreach (var screen in screens)
        {
            var widget = new FloatingWidget { ShowActivated = false };
            try
            {
                widget.Bind(snapshot);
                widget.Apply(new AppSettings { WidgetPixelLeft = screen.WorkingArea.Left + 100,
                    WidgetPixelTop = screen.WorkingArea.Top + 100 });
                widget.Show();
                foreach (var status in new[] { CodexQuotaStatus.Available, CodexQuotaStatus.Stale,
                             CodexQuotaStatus.Available })
                {
                    widget.Bind(snapshot with { Status = status });
                    for (var i = 0; i < 3; i++)
                    {
                        var frame = new DispatcherFrame();
                        widget.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                            new Action(() => frame.Continue = false));
                        Dispatcher.PushFrame(frame);
                    }
                    AssertCentered(widget, $"native {screen.DeviceName}/{status}");
                }
            }
            finally { widget.Close(); }
        }
        Console.WriteLine($"PASS: {count} widget DPI/layout renders and centered native windows on {screens.Length} monitor(s).");
    }

    private static void AssertCentered(FloatingWidget widget, string context)
    {
        var content = (FrameworkElement)widget.Content;
        var label = (TextBlock)widget.FindName("CodexLabel");
        var value = (TextBlock)widget.FindName("CodexValue");
        var row = (FrameworkElement)VisualTreeHelper.GetParent(label);
        var notice = (TextBlock)widget.FindName("HistoryValue");
        var scale = VisualTreeHelper.GetDpi(content).DpiScaleY;
        var title = (TextBlock)widget.FindName("ProductTitle");
        var top = title.TranslatePoint(new Point(), content).Y;
        var last = notice.Visibility == Visibility.Visible ? (FrameworkElement)notice : row;
        var bottom = content.ActualHeight - last.TranslatePoint(new Point(0, last.ActualHeight), content).Y;
        if (Math.Abs(top - bottom) * scale > 1.01)
            throw new InvalidOperationException($"Widget content is not centered ({context}): top={top}, bottom={bottom}.");
        var labelBaseline = label.TranslatePoint(new Point(0, label.BaselineOffset), content).Y;
        var valueBaseline = value.TranslatePoint(new Point(0, value.BaselineOffset), content).Y;
        if (Math.Abs(labelBaseline - valueBaseline) * scale > 1.01)
            throw new InvalidOperationException($"Widget label/value baselines differ ({context}): {labelBaseline}/{valueBaseline}; heights={label.ActualHeight}/{value.ActualHeight}; textDpi={VisualTreeHelper.GetDpi(label).DpiScaleY}/{VisualTreeHelper.GetDpi(value).DpiScaleY}.");
        var labelRight = label.TranslatePoint(new Point(label.ActualWidth, 0), content).X;
        var valueLeft = value.TranslatePoint(new Point(), content).X;
        if (labelRight > valueLeft + 0.01)
            throw new InvalidOperationException($"Widget label/value overlap ({context}).");
        if (Math.Abs(VisualTreeHelper.GetDpi(label).DpiScaleY - scale) > 0.001)
            throw new InvalidOperationException("Widget text did not inherit the tested DPI.");
    }

    private static RenderTargetBitmap Render(FrameworkElement content, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale),
            (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(content);
        return bitmap;
    }
}
