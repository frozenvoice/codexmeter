using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ProMeter.Codex;

namespace ProMeter;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    private AppSettings _settings = AppSettings.CreateDefaults();
    private SettingsStore _settingsStore = null!;
    private AppLog _log = null!;
    private TrayController _tray = null!;
    private CodexQuotaService _codex = null!;
    private CodexRefreshCoordinator _refresh = null!;
    private readonly CodexExecutableLocator _codexLocator = new(new WindowsCodexFileSystem());
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _codexTimer = new();
    private readonly OnceEventSubscription _widgetEvents = new();
    private FlyoutWindow? _flyout;
    private FloatingWidget? _widget;
    private TaskbarStatusStripWindow? _taskbarStrip;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Keep the legacy mutex name so an older executable cannot run beside this one.
        _mutex = new Mutex(true, @"Local\ProMeter.SingleInstance", out _ownsMutex);
        if (!_ownsMutex) { Shutdown(); return; }
        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        var firstUse = !_settings.FirstRunCompleted;
        UiText.SetLanguage(_settings.UiLanguage);
        _log = new AppLog();
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        // Retain existing preferences and history files, but never start retired collectors.
        _settings.AutoSync = false;
        _settings.CompanionConnectOptIn = false;
        _settings.FirstRunCompleted = true;
        _settingsStore.Save(_settings);
        LegacyCompanionCleanup.Unregister(_log.Warn);
        StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
        ApplyTheme(_settings.Theme);
        _tray = new TrayController();
        _tray.RebuildMenu(_settings.StartWithWindows);
        _tray.LeftClick += ToggleFlyout;
        _tray.OpenRequested += ShowMain;
        _tray.SyncRequested += () => _ = RefreshCodexAsync();
        _tray.SettingsRequested += ShowSettings;
        _tray.OpenLogsRequested += OpenLogs;
        _tray.AboutRequested += () => new AboutWindow(
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0",
            UiText.T("Codex App Server", "Codex App Server")).Show();
        _tray.StartupToggled += enabled =>
        {
            _settings.StartWithWindows = enabled;
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
            _settingsStore.Save(_settings);
        };
        _tray.ExitRequested += ExitApp;
        _codex = new CodexQuotaService(_codexLocator, new CodexAppServerClient(),
            new CodexSnapshotStore(), Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0", _log.Info);
        _refresh = new CodexRefreshCoordinator(ct => Task.Run(() => _codex.RefreshAsync(_settings.CodexExePath, ct), ct));
        _codex.Changed += _ => Dispatcher.BeginInvoke(RefreshSnapshot);
        _refresh.StateChanged += () => Dispatcher.BeginInvoke(RefreshSnapshot);
        _codexTimer.Interval = CodexQuotaService.TaskbarRefreshInterval;
        _codexTimer.Tick += async (_, _) => await RefreshCodexAsync();
        _codexTimer.Start();
        RefreshSnapshot();
        ApplyWidget();
        ApplyTaskbarStrip();
        if (firstUse || e.Args.Contains("--show", StringComparer.Ordinal)) ShowMain();
        _ = RefreshCodexAsync();
    }

    private async Task RefreshCodexAsync()
    {
        if (IsExiting) return;
        try { await _refresh.RefreshAsync(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.Error("Codex refresh failed", ex); }
    }

    private void RefreshSnapshot()
    {
        if (IsExiting || _codex is null || _refresh is null) return;
        _tray.Update(_codex.Snapshot, _settings.TrayIconStyle);
        _flyout?.Bind(_codex.Snapshot, _refresh.IsRefreshing);
        _widget?.Bind(_codex.Snapshot);
        _taskbarStrip?.Bind(_codex.Snapshot);
    }

    private void ToggleFlyout() => ToggleFlyout(FlyoutOpenSource.Tray, null, null);

    private void ToggleFlyout(FlyoutOpenSource source, Rect? anchor, TaskbarEdge? edge)
    {
        EnsureFlyout();
        if (_flyout!.IsVisible) { _flyout.Hide(); return; }
        _flyout.ApplyWindowSettings(_settings);
        RefreshSnapshot();
        _flyout.Show();
        PlaceFlyout(_flyout, source, anchor, edge);
        _flyout.Activate();
        if (CodexQuotaService.ShouldRefreshOnFlyoutOpen(_codex.Snapshot, DateTimeOffset.Now))
            _ = RefreshCodexAsync();
    }

    private void EnsureFlyout()
    {
        if (_flyout is not null) return;
        _flyout = new FlyoutWindow();
        _flyout.SyncRequested += () => _ = RefreshCodexAsync();
        _flyout.SettingsRequested += ShowSettings;
        _flyout.PinChanged += pinned => { _settings.FlyoutPinned = pinned; _settingsStore.Save(_settings); };
        _flyout.PositionChanged += (left, top) =>
        {
            _settings.FlyoutLeft = left;
            _settings.FlyoutTop = top;
            _settings.FlyoutPositionConfigured = true;
            _settingsStore.Save(_settings);
        };
    }

    private void ShowMain()
    {
        EnsureFlyout();
        if (_flyout!.IsVisible) { _flyout.Activate(); return; }
        ToggleFlyout();
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings);
        if (_flyout?.IsVisible == true) window.Owner = _flyout;
        window.Saved += settings =>
        {
            _settings = settings;
            _settingsStore.Save(settings);
            UiText.SetLanguage(settings.UiLanguage);
            ApplyTheme(settings.Theme);
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), settings);
            _tray.RebuildMenu(settings.StartWithWindows);
            _flyout?.ApplyWindowSettings(settings);
            ApplyWidget();
            ApplyTaskbarStrip();
            RefreshSnapshot();
            _ = RefreshCodexAsync();
        };
        window.OpenLogsRequested += OpenLogs;
        window.ShowDialog();
    }

    private void PlaceFlyout(FlyoutWindow flyout, FlyoutOpenSource source, Rect? anchor, TaskbarEdge? edge)
    {
        if (FlyoutWindowState.UseSavedPosition(_settings.FlyoutPositionConfigured))
        {
            flyout.RestorePosition(_settings.FlyoutLeft, _settings.FlyoutTop);
            return;
        }

        if (source == FlyoutOpenSource.TaskbarStrip && anchor is { } strip)
        {
            flyout.PlaceNear(strip, edge ?? TaskbarEdge.Bottom);
            return;
        }

        flyout.PlaceNearTaskbar();
    }

    private void OpenLogs()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _log.DirectoryPath,
            UseShellExecute = true
        });
    }

    private void ApplyTaskbarStrip()
    {
        if (!_settings.TaskbarStatusEnabled)
        {
            if (_taskbarStrip is not null)
            {
                _taskbarStrip.Close();
                _taskbarStrip = null;
            }

            return;
        }

        if (_taskbarStrip is null)
        {
            _taskbarStrip = new TaskbarStatusStripWindow
            {
                VisibilityLog = message => _log.Info(message)
            };
            _taskbarStrip.FlyoutRequested += () =>
                ToggleFlyout(FlyoutOpenSource.TaskbarStrip, _taskbarStrip.LastBounds, _taskbarStrip.LastEdge);
            _taskbarStrip.RefreshRequested += () => _ = RefreshCodexAsync();
            _taskbarStrip.ContextMenuRequested += () => _tray.ShowContextMenu();
        }

        _taskbarStrip.Bind(_codex.Snapshot);
        _taskbarStrip.Reposition();
    }

    private void ApplyWidget()
    {
        if (!_settings.FloatingWidgetEnabled)
        {
            _widget?.Hide();
            return;
        }

        _widget ??= new FloatingWidget();
        _widgetEvents.TrySubscribe(() =>
        {
            _widget.Moved += (left, top) =>
            {
                _settings.WidgetLeft = left;
                _settings.WidgetTop = top;
                _settingsStore.Save(_settings);
            };
            _widget.FlyoutRequested += ToggleFlyout;
            _widget.RefreshRequested += () => _ = RefreshCodexAsync();
            _widget.ContextMenuRequested += () => _tray.ShowContextMenu();
        });
        _widget.Apply(_settings);
        _widget.Bind(_codex.Snapshot);
        _widget.Show();
    }

    private static void ApplyTheme(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark
            || (theme == AppTheme.System && IsSystemDark());
        var app = Current;
        app.Resources["BgBrush"] = new SolidColorBrush(dark ? MediaColor(18, 20, 24) : MediaColor(245, 247, 250));
        app.Resources["CardBrush"] = new SolidColorBrush(dark ? MediaColor(27, 31, 39) : MediaColor(255, 255, 255));
        app.Resources["PanelBrush"] = new SolidColorBrush(dark ? MediaColor(33, 38, 50) : MediaColor(248, 249, 251));
        app.Resources["TextBrush"] = new SolidColorBrush(dark ? MediaColor(238, 241, 246) : MediaColor(23, 27, 34));
        app.Resources["MutedBrush"] = new SolidColorBrush(dark ? MediaColor(139, 147, 167) : MediaColor(90, 98, 114));
        app.Resources["LineBrush"] = new SolidColorBrush(dark ? MediaColor(42, 49, 64) : MediaColor(213, 218, 227));
        app.Resources["ControlBrush"] = new SolidColorBrush(dark ? MediaColor(37, 42, 52) : MediaColor(255, 255, 255));
        app.Resources["GhostBrush"] = new SolidColorBrush(dark ? MediaColor(37, 42, 52) : MediaColor(232, 236, 242));
        app.Resources["SelectionBrush"] = new SolidColorBrush(dark ? MediaColor(29, 78, 216) : MediaColor(191, 219, 254));
        app.Resources["DisabledBrush"] = new SolidColorBrush(dark ? MediaColor(107, 114, 128) : MediaColor(154, 163, 178));
        app.Resources["CheckBoxBackgroundBrush"] = new SolidColorBrush(dark ? MediaColor(37, 42, 52) : MediaColor(255, 255, 255));
        app.Resources["CheckBoxBorderBrush"] = new SolidColorBrush(dark ? MediaColor(139, 147, 167) : MediaColor(90, 98, 114));
        app.Resources["CheckBoxDisabledCheckedBrush"] = new SolidColorBrush(dark ? MediaColor(59, 82, 122) : MediaColor(147, 197, 253));
    }

    private static Color MediaColor(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }

    private async void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        _codexTimer.Stop();
        _lifetime.Cancel();
        _flyout?.Hide();
        _widget?.Hide();
        _taskbarStrip?.Close();
        // Let the existing bounded client stop and reap its app-server process.
        try { await _refresh.WaitForIdleAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { _log.Warn("Codex shutdown wait timed out"); }
        _tray.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
