using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace ProMeter;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppSettings _settings = AppSettings.CreateDefaults();
    private SettingsStore _settingsStore = null!;
    private SqliteStore _store = null!;
    private AppLog _log = null!;
    private ModelNormalizer _models = null!;
    private ConversationParser _parser = null!;
    private QuotaEngine _quota = null!;
    private SyncEngine _sync = null!;
    private ConversationExportImporter _importer = null!;
    private WebViewTransport _transport = null!;
    private ChatGptProvider _provider = null!;
    private TrayController _tray = null!;
    private ToastNotificationService _toasts = null!;
    private readonly DispatcherTimer _timer = new();
    private FlyoutWindow? _flyout;
    private MainWindow? _main;
    private FloatingWidget? _widget;
    private QuotaSnapshot _snapshot = new();
    private bool _syncing;

    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(true, @"Local\ProMeter.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            _log?.Error("unhandled UI exception", args.Exception);
            args.Handled = true;
        };

        _settingsStore = new SettingsStore();
        _settings = _settingsStore.Load();
        _store = new SqliteStore();
        _log = new AppLog();
        _models = new ModelNormalizer();
        _parser = new ConversationParser(_models);
        _quota = new QuotaEngine();
        _sync = new SyncEngine(_store, _parser, _models, _log);
        _importer = new ConversationExportImporter(_parser, _models);
        _transport = new WebViewTransport(_log);
        _provider = new ChatGptProvider(_transport);
        _toasts = new ToastNotificationService(_settingsStore, _log);
        WindowsStartupService.Apply(_settings.StartWithWindows);
        ApplyTheme(_settings.Theme);

        _tray = new TrayController();
        ToastNotificationService.Fallback = (title, body) => _tray.Balloon(title, body);
        _tray.RebuildMenu(_settings.StartWithWindows);
        _tray.LeftClick += ToggleFlyout;
        _tray.OpenRequested += ShowMain;
        _tray.StatisticsRequested += ShowMain;
        _tray.SyncRequested += () => _ = SyncAsync(true);
        _tray.LoginRequested += () => _ = SignInAsync();
        _tray.SettingsRequested += ShowSettings;
        _tray.AboutRequested += ShowAbout;
        _tray.StartupToggled += enabled =>
        {
            _settings.StartWithWindows = enabled;
            WindowsStartupService.Apply(enabled);
            _settingsStore.Save(_settings);
        };
        _tray.ExitRequested += ExitApp;

        RefreshSnapshot();
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.SyncIntervalMinutes, 5, 180));
        _timer.Tick += async (_, _) =>
        {
            if (_settings.AutoSync)
            {
                await SyncAsync(false);
            }
        };
        _timer.Start();

        if (!_settings.FirstRunCompleted)
        {
            RunWelcome();
        }
        else
        {
            _ = SyncAsync(true);
        }

        ApplyWidget();
    }

    private void RunWelcome()
    {
        var welcome = new WelcomeWindow();
        welcome.SignInRequested += async () =>
        {
            welcome.SetBusy("Opening ChatGPT sign-in...");
            await _transport.ShowLoginAsync();
            welcome.SetBusy("Detecting account...");
            _settings.ApplyPreset(welcome.SelectedPreset);
            _settingsStore.Save(_settings);
            welcome.SetBusy("Loading model catalog...");
            var outcome = await SyncAsync(true);
            welcome.SetReady($"GPT Pro usage: {_snapshot.Used} / {_snapshot.Limit}");
            _log.Info("welcome sync " + outcome.Status);
        };
        if (welcome.ShowDialog() == true)
        {
            _settings.FirstRunCompleted = true;
            _settings.ApplyPreset(welcome.SelectedPreset);
            _settingsStore.Save(_settings);
        }
    }

    private async Task<SyncOutcome> SyncAsync(bool force)
    {
        if (_syncing)
        {
            return new SyncOutcome(_snapshot.Status, "already running", 0);
        }

        _syncing = true;
        try
        {
            var outcome = await _sync.SyncAsync(_provider, _settings, force);
            RefreshSnapshot();
            if (outcome.Status is AppSyncStatus.AuthenticationRequired or AppSyncStatus.Error or AppSyncStatus.Offline)
            {
                _toasts.SyncError(_settings, outcome.Detail ?? DisplayFormatting.StatusLabel(outcome.Status));
            }

            return outcome;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshSnapshot()
    {
        var events = _store.GetUsageEvents();
        _snapshot = _quota.Build(
            events,
            _settings,
            DateTimeOffset.Now,
            _sync.LastSyncCompleted,
            _sync.LastCoverage,
            _sync.LastQuotaMetadata,
            _sync.LastStatus,
            _sync.LastStatusDetail);
        _tray.Update(_snapshot, _settings.TrayIconStyle);
        _toasts.Evaluate(_snapshot, _settings);
        _flyout?.Bind(_snapshot, _settings);
        _widget?.Bind(_snapshot);
        if (_main is { IsVisible: true })
        {
            var trend = _quota.BuildTrend(events, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(1));
            _main.Bind(_snapshot, events, trend);
        }
    }

    private void ToggleFlyout()
    {
        if (_flyout is { IsVisible: true })
        {
            _flyout.Hide();
            return;
        }

        if (_flyout is null)
        {
            _flyout = new FlyoutWindow();
            _flyout.CoverageRequested += () => new CoverageWindow(_snapshot.Coverage).Show();
        }
        _flyout.CloseOnDeactivate = _settings.FlyoutCloseOnDeactivate;
        RefreshSnapshot();
        _flyout.Bind(_snapshot, _settings);
        _flyout.Show();
        _flyout.PlaceNearTaskbar();
        _flyout.Activate();
        if (_snapshot.LastSync is null || DateTimeOffset.Now - _snapshot.LastSync > TimeSpan.FromMinutes(_settings.SyncIntervalMinutes))
        {
            _ = SyncAsync(false);
        }
    }

    private void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.SyncRequested += () => _ = SyncAsync(true);
            _main.SettingsRequested += ShowSettings;
            _main.Closing += (_, e) =>
            {
                if (!IsExiting)
                {
                    e.Cancel = true;
                    _main.Hide();
                }
            };
        }

        RefreshSnapshot();
        _main.Show();
        _main.Activate();
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings);
        window.Saved += settings =>
        {
            _settings = settings;
            _settingsStore.Save(settings);
            WindowsStartupService.Apply(settings.StartWithWindows);
            _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(settings.SyncIntervalMinutes, 5, 180));
            ApplyTheme(settings.Theme);
            _tray.RebuildMenu(settings.StartWithWindows);
            ApplyWidget();
            RefreshSnapshot();
        };
        window.ImportRequested += ImportExport;
        window.ExportRequested += format => Export(format);
        window.OpenLogsRequested += () => Process.Start(new ProcessStartInfo
        {
            FileName = _log.DirectoryPath,
            UseShellExecute = true
        });
        window.ShowDialog();
    }

    private void ImportExport()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ChatGPT export|*.json|All files|*.*",
            Title = "Import official ChatGPT conversations.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var json = File.ReadAllText(dialog.FileName);
        var importReset = _sync.LastQuotaMetadata is { MatchesGptProAllowance: true, ResetAt: not null }
            ? _sync.LastQuotaMetadata.ResetAt
            : null;
        var (start, _) = QuotaPeriodCalculator.CurrentPeriod(_settings, DateTimeOffset.Now, importReset);
        var imported = _importer.Import(json, _settings.ImportHistoricalStatistics, start);
        if (imported.Error is not null)
        {
            System.Windows.MessageBox.Show(imported.Error, "ProMeter");
            return;
        }

        _store.UpsertUsageEvents(imported.Events);
        _sync.RecordImportedEvents(imported.Events.Count);
        RefreshSnapshot();
        System.Windows.MessageBox.Show($"Imported {imported.Events.Count} usage events.", "ProMeter");
    }

    private void Export(string format)
    {
        var events = _store.GetUsageEvents();
        var dialog = new SaveFileDialog
        {
            Filter = format == "csv" ? "CSV|*.csv" : "JSON|*.json",
            FileName = format == "csv" ? "prometer-usage.csv" : "prometer-usage.json"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var text = format == "csv" ? ExportService.ToCsv(events) : ExportService.ToJson(events);
        File.WriteAllText(dialog.FileName, text);
    }

    private async Task SignInAsync()
    {
        await _transport.ShowLoginAsync();
        await SyncAsync(true);
    }

    private void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        new AboutWindow(version, DisplayFormatting.StatusLabel(_snapshot.Status)).ShowDialog();
    }

    private void ApplyWidget()
    {
        if (!_settings.FloatingWidgetEnabled)
        {
            _widget?.Hide();
            return;
        }

        _widget ??= new FloatingWidget();
        _widget.Moved += (left, top) =>
        {
            _settings.WidgetLeft = left;
            _settings.WidgetTop = top;
            _settingsStore.Save(_settings);
        };
        _widget.Apply(_settings);
        _widget.Bind(_snapshot);
        _widget.Show();
    }

    private static void ApplyTheme(AppTheme theme)
    {
        var dark = theme == AppTheme.Dark
            || (theme == AppTheme.System && IsSystemDark());
        var app = Current;
        app.Resources["BgBrush"] = new SolidColorBrush(dark ? MediaColor(18, 20, 24) : MediaColor(245, 247, 250));
        app.Resources["CardBrush"] = new SolidColorBrush(dark ? MediaColor(27, 31, 39) : MediaColor(255, 255, 255));
        app.Resources["TextBrush"] = new SolidColorBrush(dark ? MediaColor(238, 241, 246) : MediaColor(23, 27, 34));
        app.Resources["MutedBrush"] = new SolidColorBrush(dark ? MediaColor(139, 147, 167) : MediaColor(90, 98, 114));
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

    private void ExitApp()
    {
        IsExiting = true;
        _timer.Stop();
        _tray.Dispose();
        _transport.Dispose();
        _store.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
