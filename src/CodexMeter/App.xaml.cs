using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using CodexMeter.Codex;

namespace CodexMeter;

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
    private readonly DispatcherTimer _displayTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly OnceEventSubscription _widgetEvents = new();
    private Task _creditUseTask = Task.CompletedTask;
    private FlyoutWindow? _flyout;
    private FloatingWidget? _widget;
    private DesktopEnvironmentMonitor? _environment;
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Keep the legacy mutex name so an older executable cannot run beside this one.
        _mutex = new Mutex(true, LegacyInstallation.SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex) { Shutdown(); return; }
        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        var firstUse = !_settings.FirstRunCompleted;
        UiText.SetLanguage(_settings.UiLanguage);
        _log = new AppLog();
        if (_settingsStore.RecoveredFromBackup) _log.Warn("Settings restored from backup");
        DispatcherUnhandledException += (_, args) =>
        {
            _log.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        // Retain existing preferences and history files, but never start retired collectors.
        _settings.TaskbarStatusEnabled = false; // Retired overlay: Windows owns notification icon placement.
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
        _tray.CloseWidgetRequested += CloseWidget;
        _codex = new CodexQuotaService(_codexLocator, new CodexAppServerClient(),
            new CodexSnapshotStore(), Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0", _log.Info);
        _refresh = new CodexRefreshCoordinator(ct => Task.Run(() => _codex.RefreshAsync(_settings.CodexExePath, ct), ct));
        _codex.Changed += _ => Dispatcher.BeginInvoke(RefreshSnapshot);
        _refresh.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (!_refresh.IsRefreshing && !IsExiting) ApplyRefreshSchedule();
            RefreshSnapshot();
        });
        ApplyRefreshSchedule();
        _codexTimer.Tick += async (_, _) => await RefreshCodexAsync(automatic: true);

        _displayTimer.Tick += (_, _) => RefreshSnapshot();
        _displayTimer.Start();
        RefreshSnapshot();
        ApplyWidget();
        _environment = new DesktopEnvironmentMonitor(Dispatcher, OnSystemThemeChanged, OnDisplayChanged);
        if (firstUse || e.Args.Contains("--show", StringComparer.Ordinal)) ShowMain();
        _ = RefreshCodexAsync();
    }

    private void ApplyRefreshSchedule()
    {
        _codexTimer.Stop();
        _codexTimer.Interval = TimeSpan.FromMinutes(_settings.CodexRefreshIntervalMinutes);
        if (!IsExiting) _codexTimer.Start();
    }

    private async Task RefreshCodexAsync(bool automatic = false)
    {
        if (IsExiting) return;
        if (automatic && !CodexQuotaService.ShouldRefreshOnFlyoutOpen(_codex.Snapshot, DateTimeOffset.Now, _codexTimer.Interval)) return;
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
    }

    private void ToggleFlyout()
    {
        EnsureFlyout();
        if (_flyout!.IsVisible) { _flyout.Hide(); return; }
        _flyout.ApplyWindowSettings(_settings);
        RefreshSnapshot();
        _flyout.Show();
        PlaceFlyout(_flyout);
        _flyout.Activate();
        if (CodexQuotaService.ShouldRefreshOnFlyoutOpen(_codex.Snapshot, DateTimeOffset.Now, _codexTimer.Interval))
            _ = RefreshCodexAsync(automatic: true);
    }

    private void EnsureFlyout()
    {
        if (_flyout is not null) return;
        _flyout = new FlyoutWindow();
        _flyout.RedeemCredit = async creditId =>
        {
            if (IsExiting) return CreditRedemptionOutcome.Unavailable;
            var useTask = Task.Run(() => _codex.ConsumeCreditAsync(creditId, _settings.CodexExePath, _lifetime.Token));
            _creditUseTask = useTask;
            var outcome = await useTask;
            if (IsExiting) return outcome;
            await _refresh.WaitForIdleAsync();
            await RefreshCodexAsync();
            return outcome;
        };
        _flyout.SyncRequested += () => _ = RefreshCodexAsync();
        _flyout.SettingsRequested += ShowSettings;
        _flyout.PinChanged += pinned => { _settings.FlyoutPinned = pinned; _settingsStore.Save(_settings); };
        _flyout.ZoomChanged += percent => { _settings.FlyoutZoomPercent = percent; _settingsStore.Save(_settings); };
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
            if (window.ResetWidgetPositionOnSave)
            {
                var work = SystemParameters.WorkArea;
                settings.WidgetPixelLeft = null;
                settings.WidgetPixelTop = null;
                settings.WidgetLeft = work.Left + 40;
                settings.WidgetTop = work.Top + 40;
            }
            _settings = settings;
            _settingsStore.Save(settings);
            ApplyRefreshSchedule();
            UiText.SetLanguage(settings.UiLanguage);
            ApplyTheme(settings.Theme);
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), settings);
            _tray.RebuildMenu(settings.StartWithWindows);
            _flyout?.ApplyWindowSettings(settings);
            ApplyWidget();
            RefreshSnapshot();
            _ = RefreshCodexAsync();
        };
        window.OpenLogsRequested += OpenLogs;
        window.ShowDialog();
    }

    private void PlaceFlyout(FlyoutWindow flyout)
    {
        if (FlyoutWindowState.UseSavedPosition(_settings.FlyoutPositionConfigured))
        {
            flyout.RestorePosition(_settings.FlyoutLeft, _settings.FlyoutTop);
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

    private void CloseWidget()
    {
        _settings.FloatingWidgetEnabled = false;
        _settingsStore.Save(_settings);
        ApplyWidget();
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
                if (_widget.PixelPosition is { } pixels)
                {
                    _settings.WidgetPixelLeft = pixels.X;
                    _settings.WidgetPixelTop = pixels.Y;
                }
                _settingsStore.Save(_settings);
            };
            _widget.FlyoutRequested += ToggleFlyout;
            _widget.RefreshRequested += () => _ = RefreshCodexAsync();
            _widget.ContextMenuRequested += () => _tray.ShowWidgetContextMenu();
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

    private void OnSystemThemeChanged()
    {
        if (IsExiting || _settings.Theme != AppTheme.System) return;
        ApplyTheme(AppTheme.System);
        RefreshSnapshot();
    }

    private void OnDisplayChanged()
    {
        if (IsExiting) return;
        _widget?.RecoverPosition();
        _flyout?.RefreshWorkArea();
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
        _environment?.Dispose();
        _codexTimer.Stop();
        _displayTimer.Stop();
        _lifetime.Cancel();
        _flyout?.Hide();
        _widget?.Hide();
        // Let the existing bounded client stop and reap its app-server process.
        try { await Task.WhenAll(_refresh.WaitForIdleAsync(), _creditUseTask).WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { _log.Warn("Codex shutdown wait timed out"); }
        _tray.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _environment?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
