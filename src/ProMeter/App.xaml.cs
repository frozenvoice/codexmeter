using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ProMeter.Codex;
using ProMeter.Companion;
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
    private WebViewTransport _webViewTransport = null!;
    private IChatGptTransport _transport = null!;
    private ChatGptProvider _provider = null!;
    private CompanionRequestHub _companionHub = null!;
    private CompanionPipeServer? _companionServer;
    private TrayController _tray = null!;
    private ToastNotificationService _toasts = null!;
    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _codexTimer = new();
    private readonly DispatcherTimer _proStatusTimer = new();
    private readonly DispatcherTimer _proResetRecheckTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private FlyoutWindow? _flyout;
    private MainWindow? _main;
    private FloatingWidget? _widget;
    private TaskbarStatusStripWindow? _taskbarStrip;
    private QuotaSnapshot _snapshot = new();
    private bool _syncing;
    private CodexQuotaService _codex = null!;
    private CombinedRefreshCoordinator _refresh = null!;
    private ProServerStatusService _proStatus = null!;
    private readonly OnceEventSubscription _widgetEvents = new();
    private readonly CodexExecutableLocator _codexLocator = new(new WindowsCodexFileSystem());
    private readonly CancellationTokenSource _lifetime = new();

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
        UiText.SetLanguage(_settings.UiLanguage);
        _store = new SqliteStore();
        _log = new AppLog();
        _models = new ModelNormalizer();
        _parser = new ConversationParser(_models);
        _quota = new QuotaEngine();
        _sync = new SyncEngine(_store, _parser, _models, _log);
        _sync.ProgressChanged += _ => Dispatcher.BeginInvoke(RefreshSnapshot);
        _importer = new ConversationExportImporter(_parser, _models);
        _companionHub = new CompanionRequestHub();
        _webViewTransport = new WebViewTransport(_log);
        var pairing = CompanionPairingStore.LoadOrCreate();
        _companionServer = new CompanionPipeServer(_companionHub, pairing, _log);
        _companionServer.Start();
        ApplyTransport();
        _toasts = new ToastNotificationService(_settingsStore, _log);
        StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
        ApplyTheme(_settings.Theme);

        _tray = new TrayController();
        ToastNotificationService.Fallback = (title, body) => _tray.Balloon(title, body);
        _tray.RebuildMenu(_settings.StartWithWindows);
        _tray.LeftClick += ToggleFlyout;
        _tray.OpenRequested += ShowMain;
        _tray.StatisticsRequested += ShowMain;
        _tray.SyncRequested += () => _ = RefreshAllAsync(true, _lifetime.Token);
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        _codex = new CodexQuotaService(
            _codexLocator,
            new CodexAppServerClient(),
            new CodexSnapshotStore(),
            version,
            message => _log.Info(message));
        _codex.Changed += _ => Dispatcher.BeginInvoke(RefreshSnapshot);
        _refresh = new CombinedRefreshCoordinator(
            (bypassPause, _) => SyncAsync(SyncRunOptions.Manual(bypassPause)),
            ct => _codex.RefreshAsync(_settings.CodexExePath, ct));
        _refresh.StateChanged += () => Dispatcher.BeginInvoke(RefreshSnapshot);
        _proStatus = new ProServerStatusService();
        _proStatus.Changed += _ => Dispatcher.BeginInvoke(RefreshSnapshot);
        _tray.LoginRequested += () => _ = SignInAsync();
        _tray.SettingsRequested += ShowSettings;
        _tray.OpenLogsRequested += OpenLogs;
        _tray.AboutRequested += ShowAbout;
        _tray.StartupToggled += enabled =>
        {
            _settings.StartWithWindows = enabled;
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
            _settingsStore.Save(_settings);
        };
        _tray.ExitRequested += ExitApp;

        RefreshSnapshot();
        _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.SyncIntervalMinutes, 5, 180));
        _timer.Tick += async (_, _) =>
        {
            if (_settings.AutoSync)
            {
                await SyncAsync(SyncRunOptions.Auto);
            }
        };
        _timer.Start();
        _codexTimer.Interval = CodexQuotaService.TaskbarRefreshInterval;
        _codexTimer.Tick += async (_, _) =>
        {
            if (_settings.TaskbarStatusEnabled)
            {
                await _codex.RefreshAsync(_settings.CodexExePath, _lifetime.Token);
            }
        };
        _proStatusTimer.Interval = ProServerStatusService.RefreshInterval;
        _proStatusTimer.Tick += async (_, _) =>
        {
            if (ProServerStatusService.ShouldPeriodicRefresh(_settings)
                && _settings.AuthTransport == AuthTransportKind.BrowserCompanion)
            {
                await RefreshProStatusAsync(notifyFailure: false);
            }
        };
        _proResetRecheckTimer.Tick += async (_, _) =>
        {
            _proResetRecheckTimer.Stop();
            if (_settings.AuthTransport == AuthTransportKind.BrowserCompanion)
            {
                await RefreshProStatusAsync(notifyFailure: false);
            }
        };
        ApplyCodexTimer();
        ApplyProStatusTimer();

        if (!_settings.FirstRunCompleted)
        {
            RunWelcome();
        }
        else if (_settings.AutoSync)
        {
            _ = SyncAsync(SyncRunOptions.StartupIncremental);
        }

        ApplyWidget();
        ApplyTaskbarStrip();
        var startupCodex = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        startupCodex.Tick += async (_, _) =>
        {
            startupCodex.Stop();
            await _codex.RefreshAsync(_settings.CodexExePath, _lifetime.Token);
        };
        startupCodex.Start();
        if (_settings.AuthTransport == AuthTransportKind.BrowserCompanion)
        {
            var startupStatus = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            startupStatus.Tick += async (_, _) =>
            {
                startupStatus.Stop();
                await RefreshProStatusAsync(notifyFailure: false);
            };
            startupStatus.Start();
        }
    }

    private void RunWelcome()
    {
        var welcome = new WelcomeWindow();
        var adapter = new WelcomeCompanionConnectionAdapter(
            new DispatcherUiMarshal(welcome.Dispatcher),
            () => welcome.CompanionRegistered,
            welcome.SetCompanionState);
        void OnCompanionConnectionChanged() => adapter.HandleConnectionChanged(_companionHub.IsConnected);
        _companionHub.ConnectionChanged += OnCompanionConnectionChanged;
        welcome.Closed += (_, _) =>
        {
            adapter.Detach();
            _companionHub.ConnectionChanged -= OnCompanionConnectionChanged;
        };
        welcome.OpenExtensionFolderRequested += () =>
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "extension");
            if (!Directory.Exists(folder))
            {
                folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "extension"));
            }

            if (Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
            }
        };
        welcome.RegisterCompanionRequested += (chromeId, edgeId) =>
        {
            ApplyWelcomeChoices(welcome);
            var error = RegisterCompanionHost(chromeId, edgeId);
            if (error is not null)
            {
                welcome.SetCompanionState(UiText.CompanionNotInstalledPrefix + error, registered: false, connected: false);
                return;
            }

            welcome.SetCompanionState(
                _companionHub.IsConnected ? UiText.CompanionConnected : UiText.CompanionRegisteredWaiting,
                registered: true,
                connected: _companionHub.IsConnected);
        };
        welcome.OpenChatGptRequested += () => Process.Start(new ProcessStartInfo
        {
            FileName = ChatGptEndpoints.LoginUrl,
            UseShellExecute = true
        });
        welcome.SignInRequested += async () =>
        {
            ApplyWelcomeChoices(welcome);
            _settingsStore.Save(_settings);
            ApplyTransport();
            if (_settings.AuthTransport == AuthTransportKind.DataExport)
            {
                welcome.MarkSignedIn(UiText.DataExportSelected);
                return;
            }

            if (_settings.AuthTransport == AuthTransportKind.BrowserCompanion)
            {
                Process.Start(new ProcessStartInfo { FileName = ChatGptEndpoints.LoginUrl, UseShellExecute = true });
                welcome.SetCompanionState(
                    _companionHub.IsConnected ? UiText.CompanionConnected : (welcome.CompanionRegistered ? UiText.CompanionWaiting : UiText.CompanionNotInstalled),
                    welcome.CompanionRegistered,
                    _companionHub.IsConnected);
                return;
            }

            welcome.SetBusy(UiText.OpeningWebViewSignIn);
            var signedIn = await _webViewTransport.ShowLoginAsync();
            if (!signedIn)
            {
                welcome.SetCancelled(UiText.SignInCancelled);
                return;
            }

            welcome.MarkSignedIn(UiText.SignedInRunSync);
        };
        welcome.SyncRequested += async () =>
        {
            ApplyWelcomeChoices(welcome);
            _settingsStore.Save(_settings);
            ApplyTransport();
            if (_settings.AuthTransport == AuthTransportKind.BrowserCompanion && !_companionHub.IsConnected)
            {
                welcome.SetCompanionState(UiText.CompanionWaiting, welcome.CompanionRegistered, false);
                return;
            }

            welcome.SetBusy(UiText.RunningFirstSync);
            var outcome = await SyncAsync(SyncRunOptions.ManualIncremental);
            welcome.ApplyOutcome(OnboardingOutcomeMapper.From(
                outcome.Status,
                _snapshot.Used,
                _snapshot.Limit,
                outcome.Detail,
                _snapshot.DisplayUsageUnavailable,
                _snapshot.UsesServerCount));
            _log.Info("welcome sync " + outcome.Status);
        };
        if (welcome.ShowDialog() == true)
        {
            _settings.FirstRunCompleted = true;
            ApplyWelcomeChoices(welcome);
            _settingsStore.Save(_settings);
            ApplyTransport();
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), _settings);
        }
    }

    private async Task<SyncOutcome> SyncAsync(SyncRunOptions options)
    {
        if (_syncing)
        {
            return new SyncOutcome(_snapshot.Status, "already running", 0);
        }

        _syncing = true;
        RefreshSnapshot();
        try
        {
            var outcome = await _sync.SyncAsync(_provider, _settings, options);
            _proStatus.ApplyFromMetadata(_sync.LastQuotaMetadata);
            ScheduleProResetRecheck();
            ApplySyncFailurePresentation(outcome, options.Origin);
            return outcome;
        }
        finally
        {
            _syncing = false;
            RefreshSnapshot();
        }
    }

    private void ApplySyncFailurePresentation(SyncOutcome outcome, SyncOrigin origin)
    {
        var now = DateTimeOffset.Now;
        if (SyncFailurePresentation.IsLoggedFailure(outcome.Status))
        {
            SyncErrorToastState.RememberFailureAttempt(_settings, outcome.Status, now);
            if (SyncFailurePresentation.ProducesSyncErrorToast(outcome.Status))
            {
                _toasts.TrySyncError(
                    _settings,
                    outcome.Status,
                    outcome.Detail ?? DisplayFormatting.StatusLabel(outcome.Status),
                    origin,
                    now);
            }

            _settingsStore.Save(_settings);
            return;
        }

        if (SyncFailurePresentation.IsSuccessfulCompletion(outcome.Status))
        {
            _toasts.ResetSyncErrorSuppression(_settings);
            _settingsStore.Save(_settings);
        }
    }

    private void RefreshSnapshot()
    {
        var events = _store.GetUsageEvents();
        var metadata = _sync.LastQuotaMetadata ?? new QuotaMetadataSet();
        if (_proStatus.Current.ServerObserved)
        {
            metadata.ProServerStatus = _proStatus.Current;
        }

        _snapshot = _quota.Build(
            events,
            _settings,
            DateTimeOffset.Now,
            _sync.LastSyncCompleted,
            _sync.LastCoverage,
            metadata,
            _sync.LastStatus,
            _sync.LastStatusDetail);
        _snapshot.IsSyncing = _syncing;
        if (_syncing)
        {
            _snapshot.Status = AppSyncStatus.Syncing;
        }
        _tray.Update(_snapshot, _settings.TrayIconStyle);
        _toasts.Evaluate(_snapshot, _settings);
        _flyout?.Bind(
            _snapshot,
            _settings,
            _codex.Snapshot,
            _syncing || _refresh.ChatGptRefreshing,
            _codex.IsRefreshing || _refresh.CodexRefreshing,
            _refresh.ManualRefreshInProgress);
        _widget?.Bind(_snapshot, _codex.Snapshot);
        _taskbarStrip?.Bind(_snapshot, _codex.Snapshot);
        if (_main is { IsVisible: true })
        {
            var trend = _quota.BuildTrend(events, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(1));
            _main.Bind(_snapshot, events, trend, _codex.Snapshot);
        }
    }

    private async Task RefreshAllAsync(bool bypassPause, CancellationToken cancellationToken)
    {
        var status = RefreshProStatusAsync(notifyFailure: false);
        await _refresh.RefreshAllAsync(bypassPause, cancellationToken);
        await status;
    }

    private void ToggleFlyout() => ToggleFlyout(FlyoutOpenSource.Tray, null, null);

    private void ToggleFlyout(FlyoutOpenSource source, Rect? anchor, TaskbarEdge? edge)
    {
        if (_flyout is { IsVisible: true })
        {
            _flyout.Hide();
            return;
        }

        if (_flyout is null)
        {
            _flyout = new FlyoutWindow();
            _flyout.CoverageRequested += () => new CoverageWindow(
                _snapshot.Coverage,
                _codex.Snapshot,
                _codexLocator.Locate(_settings.CodexExePath) is not null).Show();
            _flyout.SyncRequested += () => _ = RefreshAllAsync(true, _lifetime.Token);
        }
        _flyout.CloseOnDeactivate = _settings.FlyoutCloseOnDeactivate;
        RefreshSnapshot();
        _flyout.Show();
        if (source == FlyoutOpenSource.TaskbarStrip && anchor is { } strip)
        {
            _flyout.PlaceNear(strip, edge ?? TaskbarEdge.Bottom);
        }
        else
        {
            _flyout.PlaceNearTaskbar();
        }

        _flyout.Activate();
        if (FlyoutAutoSyncPolicy.ShouldStartStaleAutoSync(
                _settings.AutoSync,
                source == FlyoutOpenSource.TaskbarStrip,
                DateTimeOffset.Now,
                _snapshot.LastSync,
                _settings.SyncIntervalMinutes,
                FlyoutAutoSyncPolicy.ParseTimestamp(_settings.LastSyncFailureAt)))
        {
            _ = SyncAsync(SyncRunOptions.FlyoutStale);
        }

        if (CodexQuotaService.ShouldRefreshOnFlyoutOpen(_codex.Snapshot, DateTimeOffset.Now))
        {
            _ = _codex.RefreshAsync(_settings.CodexExePath, _lifetime.Token);
        }

        if (_settings.AuthTransport == AuthTransportKind.BrowserCompanion
            && ProServerStatusService.ShouldRefreshOnFlyoutOpen(_proStatus.Current, DateTimeOffset.Now))
        {
            _ = RefreshProStatusAsync(notifyFailure: false);
        }
    }

    private void ShowMain()
    {
        if (_main is null)
        {
            _main = new MainWindow();
            _main.SyncRequested += () => _ = RefreshAllAsync(true, _lifetime.Token);
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
            UiText.SetLanguage(settings.UiLanguage);
            ApplyTransport();
            StartupConsent.ApplyIfPermitted(new WindowsStartupService(), settings);
            _timer.Interval = TimeSpan.FromMinutes(Math.Clamp(settings.SyncIntervalMinutes, 5, 180));
            ApplyTheme(settings.Theme);
            _tray.RebuildMenu(settings.StartWithWindows);
            _flyout?.ApplyLocalizedTexts();
            _main?.ApplyLocalizedTexts();
            ApplyWidget();
            ApplyCodexTimer();
            ApplyProStatusTimer();
            ApplyTaskbarStrip();
            _taskbarStrip?.ApplyThemeResources();
            RefreshSnapshot();
        };
        window.CompanionRegisterRequested += (chromeId, edgeId) =>
        {
            var error = RegisterCompanionHost(chromeId, edgeId);
            MessageBox.Show(
                error ?? (UiText.RegisteredNativeHosts + CompanionRegistration.WhaleInstructions),
                UiText.ProductName);
        };
        window.ImportRequested += ImportExport;
        window.ExportRequested += format => Export(format);
        window.OpenLogsRequested += OpenLogs;
        window.ShowDialog();
    }

    private void OpenLogs()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = _log.DirectoryPath,
            UseShellExecute = true
        });
    }

    private void ImportExport()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ChatGPT export|*.json|All files|*.*",
            Title = UiText.ImportTitle
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var json = File.ReadAllText(dialog.FileName);
        var importReset = _sync.LastQuotaMetadata?.WeeklyWindow(_settings.PlanPreset)?.ResetAt;
        var (start, _) = QuotaPeriodCalculator.CurrentPeriod(_settings, DateTimeOffset.Now, importReset);
        var imported = _importer.Import(json, _settings.ImportHistoricalStatistics, start);
        if (imported.Error is not null)
        {
            System.Windows.MessageBox.Show(imported.Error, UiText.ProductName);
            return;
        }

        _store.UpsertUsageEvents(imported.Events);
        _sync.RecordImportedEvents(imported.Events.Count);
        RefreshSnapshot();
        System.Windows.MessageBox.Show(UiText.ImportedEvents(imported.Events.Count), UiText.ProductName);
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
        if (_syncing)
        {
            return;
        }

        if (_settings.AuthTransport == AuthTransportKind.DataExport)
        {
            return;
        }

        if (_settings.AuthTransport == AuthTransportKind.BrowserCompanion)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ChatGptEndpoints.LoginUrl,
                UseShellExecute = true
            });
            return;
        }

        if (!await _webViewTransport.ShowLoginAsync())
        {
            return;
        }

        if (_settings.AutoSync)
        {
            await SyncAsync(SyncRunOptions.ManualIncremental);
        }
    }

    private void ApplyWelcomeChoices(WelcomeWindow welcome)
    {
        _settings.ApplyPreset(welcome.SelectedPreset);
        _settings.AuthTransport = welcome.SelectedTransport;
        _settings.ChromeExtensionId = welcome.ChromeExtensionId;
        _settings.EdgeExtensionId = welcome.EdgeExtensionId;
        _settings.CompanionExtensionId = welcome.ChromeExtensionId ?? welcome.EdgeExtensionId;
        _settings.StartWithWindows = welcome.StartWithWindowsOptIn;
        _settings.AutoSync = welcome.AutoSyncOptIn;
    }

    private string? RegisterCompanionHost(string? chromeId, string? edgeId)
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "prometer.exe");
        var result = CompanionRegistration.Register(exe, chromeId, edgeId);
        if (!result.Ok)
        {
            return result.Error ?? UiText.NativeHostFailed;
        }

        _settings.ChromeExtensionId = chromeId;
        _settings.EdgeExtensionId = edgeId;
        _settings.CompanionExtensionId = chromeId ?? edgeId;
        var pairing = CompanionPairingStore.LoadOrCreate();
        pairing.ChromeExtensionId = chromeId;
        pairing.EdgeExtensionId = edgeId;
        pairing.ExtensionId = chromeId ?? edgeId;
        CompanionPairingStore.Save(pairing);
        _settings.CompanionConnectOptIn = true;
        _settingsStore.Save(_settings);
        return null;
    }

    private void ApplyTransport()
    {
        _transport = _settings.AuthTransport switch
        {
            AuthTransportKind.BrowserCompanion => new BrowserCompanionTransport(
                _companionHub,
                message => _log.Info(message)),
            AuthTransportKind.DataExport => new DataExportTransport(),
            _ => _webViewTransport
        };
        _provider = new ChatGptProvider(_transport);
    }

    private void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        new AboutWindow(version, DisplayFormatting.StatusLabel(_snapshot.Status)).ShowDialog();
    }

    private void ApplyCodexTimer()
    {
        if (_settings.TaskbarStatusEnabled)
        {
            _codexTimer.Start();
        }
        else
        {
            _codexTimer.Stop();
        }
    }

    private void ApplyProStatusTimer()
    {
        if (ProServerStatusService.ShouldPeriodicRefresh(_settings)
            && _settings.AuthTransport == AuthTransportKind.BrowserCompanion)
        {
            _proStatusTimer.Start();
            ScheduleProResetRecheck();
        }
        else
        {
            _proStatusTimer.Stop();
            _proResetRecheckTimer.Stop();
        }
    }

    private void ScheduleProResetRecheck()
    {
        _proResetRecheckTimer.Stop();
        var due = ProServerStatusService.NextResetRecheck(_proStatus.Current, DateTimeOffset.Now);
        if (due is null)
        {
            return;
        }

        var delay = due.Value - DateTimeOffset.Now;
        if (delay < TimeSpan.Zero)
        {
            delay = ProServerStatusService.ResetRecheckMin;
        }

        _proResetRecheckTimer.Interval = delay;
        _proResetRecheckTimer.Start();
    }

    private async Task RefreshProStatusAsync(bool notifyFailure)
    {
        try
        {
            await _proStatus.RefreshAsync(_provider, _lifetime.Token);
            ScheduleProResetRecheck();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (notifyFailure)
            {
                _log.Warn("pro status refresh failed: " + AppLog.Sanitize(ex.Message));
            }
        }
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
            _taskbarStrip.RefreshRequested += () => _ = RefreshAllAsync(true, _lifetime.Token);
            _taskbarStrip.ContextMenuRequested += () => _tray.ShowContextMenu();
        }

        _taskbarStrip.Bind(_snapshot, _codex.Snapshot);
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
            _widget.RefreshRequested += () => _ = RefreshAllAsync(true, _lifetime.Token);
            _widget.ContextMenuRequested += () => _tray.ShowContextMenu();
        });
        _widget.Apply(_settings);
        _widget.Bind(_snapshot, _codex.Snapshot);
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

    private void ExitApp()
    {
        IsExiting = true;
        _lifetime.Cancel();
        _timer.Stop();
        _codexTimer.Stop();
        _proStatusTimer.Stop();
        _proResetRecheckTimer.Stop();
        _taskbarStrip?.Close();
        _taskbarStrip = null;
        _tray.Dispose();
        _companionServer?.Dispose();
        _webViewTransport.Dispose();
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
