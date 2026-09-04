using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.WebView;

public sealed class WebViewTransport : IChatGptTransport, IDisposable
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(15);

    private readonly AppLog _log;
    private readonly Window _host;
    private readonly WebView2 _webView;
    private readonly SemaphoreSlim _ready = new(0, 1);
    private readonly SessionAuthCoordinator _auth = new();
    private readonly LoginNavigationMachine _login = new();
    private TaskCompletionSource<bool>? _navigationSignal;
    private int _probeGeneration;
    private bool _initialized;
    private bool _shownForLogin;

    public WebViewTransport(AppLog log)
    {
        _log = log;
        _webView = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(18, 20, 24) };
        _host = new Window
        {
            Title = "ProMeter — ChatGPT sign-in",
            Width = 980,
            Height = 720,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = _webView,
            ShowInTaskbar = true,
            Visibility = Visibility.Hidden
        };
        _host.Closing += (_, e) =>
        {
            if (System.Windows.Application.Current is App { IsExiting: true })
            {
                return;
            }

            e.Cancel = true;
            HideLogin();
            _login.Cancel();
        };
    }

    public bool IsLoginVisible => _shownForLogin && _host.IsVisible;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: AppPaths.WebViewProfile);
        await _webView.EnsureCoreWebView2Async(env);
        _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        _webView.CoreWebView2.SourceChanged += OnSourceChanged;
        _webView.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        _webView.CoreWebView2.Navigate(ChatGptEndpoints.HomeUrl);
        await _ready.WaitAsync();
        _initialized = true;
    }

    public async Task<bool> ShowLoginAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        var (wait, started) = _login.BeginOrJoin();
        if (!started)
        {
            return await wait;
        }

        _auth.Invalidate();
        _probeGeneration = _login.Generation;
        _shownForLogin = true;
        _host.Show();
        _host.Activate();
        _webView.CoreWebView2.Navigate(ChatGptEndpoints.LoginUrl);
        using var reg = cancellationToken.Register(() =>
        {
            HideLogin();
            _login.Cancel();
        });

        try
        {
            return await wait;
        }
        catch (OperationCanceledException)
        {
            _login.Cancel();
            return false;
        }
    }

    public void HideLogin()
    {
        _shownForLogin = false;
        _host.Hide();
    }

    public async Task<AccountStatus> ProbeSessionAsync(CancellationToken cancellationToken = default)
    {
        if (!SessionProbePolicy.CanProbe(CurrentUri))
        {
            return new AccountStatus();
        }

        var response = await SendAsync("GET", ChatGptEndpoints.Session, cancellationToken: cancellationToken);
        if (!response.IsSuccess)
        {
            return new AccountStatus();
        }

        return AccountParser.ParseSession(ChatGptJson.ParseNode(response.Body));
    }

    public async Task<ProviderResponse> SendAsync(string method, string path, string? jsonBody = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        if (!BackendTargetPolicy.TryValidate(path, out var safePath, out var targetError))
        {
            return new ProviderResponse { Status = 0, Error = targetError, SchemaMismatch = true };
        }

        if (_login.IsActive && !OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return new ProviderResponse { Status = 0, Error = "backend fetch blocked during interactive login" };
        }

        await EnsureOriginAsync(cancellationToken);
        if (!OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return new ProviderResponse { Status = 0, Error = "unvalidated origin", SchemaMismatch = true };
        }

        return await _auth.SendAsync(ExecuteRawAsync, method, safePath, jsonBody, cancellationToken);
    }

    private async Task EnsureOriginAsync(CancellationToken cancellationToken)
    {
        if (!_login.MayNavigateAwayToProbe())
        {
            return;
        }

        if (OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return;
        }

        await NavigateAndWaitAsync(ChatGptEndpoints.HomeUrl, cancellationToken);
    }

    private async Task NavigateAndWaitAsync(string url, CancellationToken cancellationToken)
    {
        _navigationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _webView.CoreWebView2.Navigate(url);
        using var reg = cancellationToken.Register(() => _navigationSignal.TrySetCanceled(cancellationToken));
        var completed = await Task.WhenAny(_navigationSignal.Task, Task.Delay(NavigationTimeout, cancellationToken));
        if (completed != _navigationSignal.Task)
        {
            _log.Warn("navigation wait timed out");
        }
    }

    private void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        _login.Observe(CurrentUri, navigationCompleted: false);
        if (_login.ShouldStopProbe(CurrentUri))
        {
            _probeGeneration = -1;
        }
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (!_login.IsActive)
        {
            if (OriginPolicy.AllowsBackendFetch(e.Uri))
            {
                _webView.CoreWebView2.Navigate(e.Uri);
            }

            return;
        }

        if (OriginPolicy.AllowsInteractiveNavigation(e.Uri))
        {
            _webView.CoreWebView2.Navigate(e.Uri);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_ready.CurrentCount == 0)
        {
            try { _ready.Release(); } catch (SemaphoreFullException) { }
        }

        _navigationSignal?.TrySetResult(e.IsSuccess);
        var generation = _login.Generation;
        if (!_login.AcceptsProbe(generation, CurrentUri, navigationCompleted: true))
        {
            return;
        }

        try
        {
            foreach (var delay in SessionProbePolicy.Backoff)
            {
                if (generation != _login.Generation || _login.ShouldStopProbe(CurrentUri))
                {
                    return;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay);
                }

                if (generation != _login.Generation || !SessionProbePolicy.CanProbe(CurrentUri))
                {
                    return;
                }

                var status = await ProbeSessionAsync();
                if (status.IsSignedIn && generation == _login.Generation)
                {
                    HideLogin();
                    _login.Complete(true);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("login session probe failed: " + AppLog.Sanitize(ex.Message));
        }
    }

    private Task<ProviderResponse> ExecuteRawAsync(
        string method,
        string path,
        string? jsonBody,
        string? accessToken,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (!BackendTargetPolicy.TryValidate(path, out var safePath, out var error)
            || !OriginPolicy.AllowsBackendFetch(CurrentUri))
        {
            return Task.FromResult(new ProviderResponse { Status = 0, Error = error, SchemaMismatch = true });
        }

        return ExecuteScriptFetchAsync(method, safePath, jsonBody, accessToken);
    }

    private async Task<ProviderResponse> ExecuteScriptFetchAsync(string method, string path, string? body, string? accessToken)
    {
        var script = BuildFetchScript(method, path, body, accessToken);
        try
        {
            var raw = await _webView.ExecuteScriptAsync(script);
            var decoded = DecodeScriptResult(raw);
            var node = ChatGptJson.ParseNode(decoded);
            if (node is null)
            {
                return new ProviderResponse { Status = 0, Error = "empty transport result", SchemaMismatch = true };
            }

            return new ProviderResponse
            {
                Status = (int)(ChatGptJson.GetDouble(node, "status") ?? 0),
                RetryAfter = ChatGptJson.GetString(node, "retryAfter"),
                Body = ChatGptJson.GetString(node, "body") ?? "",
                Error = ChatGptJson.GetString(node, "error"),
                SchemaMismatch = ChatGptJson.GetBool(node, "schemaMismatch") == true
            };
        }
        catch (Exception ex)
        {
            _log.Error("webview fetch failed", ex);
            return new ProviderResponse { Status = 0, Error = "offline" };
        }
    }

    private static string BuildFetchScript(string method, string path, string? body, string? accessToken)
    {
        var methodJson = JsonSerializer.Serialize(method);
        var pathJson = JsonSerializer.Serialize(path);
        var bodyJson = body is null ? "null" : JsonSerializer.Serialize(body);
        var tokenJson = accessToken is null ? "null" : JsonSerializer.Serialize(accessToken);
        return $$"""
            (async () => {
              try {
                const token = {{tokenJson}};
                const headers = { 'Accept': 'application/json' };
                if (token) headers['Authorization'] = 'Bearer ' + token;
                const init = { method: {{methodJson}}, credentials: 'include', headers };
                const body = {{bodyJson}};
                if (body) {
                  headers['Content-Type'] = 'application/json';
                  init.body = body;
                }
                const res = await fetch({{pathJson}}, init);
                const text = await res.text();
                return JSON.stringify({
                  status: res.status,
                  retryAfter: res.headers.get('retry-after'),
                  body: text
                });
              } catch (error) {
                return JSON.stringify({ status: 0, error: String(error && error.message ? error.message : error) });
              }
            })()
            """;
    }

    private string? CurrentUri => _webView.Source?.AbsoluteUri;

    private static string DecodeScriptResult(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return "";
        }

        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? raw.Trim('"');
        }
        catch
        {
            return raw;
        }
    }

    public void Dispose()
    {
        _webView.Dispose();
    }
}
