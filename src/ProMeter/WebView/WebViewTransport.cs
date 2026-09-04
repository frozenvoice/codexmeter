using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.WebView;

public sealed class WebViewTransport : IChatGptTransport, IDisposable
{
    private readonly AppLog _log;
    private readonly Window _host;
    private readonly WebView2 _webView;
    private readonly SemaphoreSlim _ready = new(0, 1);
    private TaskCompletionSource<bool>? _loginSignal;
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
            _loginSignal?.TrySetResult(false);
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
        _webView.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            if (_ready.CurrentCount == 0)
            {
                try { _ready.Release(); } catch (SemaphoreFullException) { }
            }
        };
        _webView.CoreWebView2.Navigate(ChatGptEndpoints.HomeUrl);
        await _ready.WaitAsync();
        _initialized = true;
    }

    public async Task ShowLoginAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync();
        _loginSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _shownForLogin = true;
        _host.Show();
        _host.Activate();
        _webView.CoreWebView2.Navigate(ChatGptEndpoints.LoginUrl);
        using var reg = cancellationToken.Register(() => _loginSignal.TrySetCanceled(cancellationToken));
        while (!cancellationToken.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(_loginSignal.Task, Task.Delay(1500, cancellationToken));
            var status = await ProbeSessionAsync(cancellationToken);
            if (status.IsSignedIn)
            {
                HideLogin();
                _loginSignal.TrySetResult(true);
                return;
            }

            if (completed == _loginSignal.Task && !status.IsSignedIn)
            {
                return;
            }
        }
    }

    public void HideLogin()
    {
        _shownForLogin = false;
        _host.Hide();
    }

    public async Task<AccountStatus> ProbeSessionAsync(CancellationToken cancellationToken = default)
    {
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
        await EnsureOriginAsync(cancellationToken);
        var script = BuildFetchScript(method, path, jsonBody);
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
                Error = ChatGptJson.GetString(node, "error")
            };
        }
        catch (Exception ex)
        {
            _log.Error("webview fetch failed", ex);
            return new ProviderResponse { Status = 0, Error = "offline" };
        }
    }

    private async Task EnsureOriginAsync(CancellationToken cancellationToken)
    {
        var source = _webView.Source?.Host ?? "";
        if (!source.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase)
            && !source.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            _webView.CoreWebView2.Navigate(ChatGptEndpoints.HomeUrl);
            await Task.Delay(800, cancellationToken);
        }
    }

    private static string BuildFetchScript(string method, string path, string? body)
    {
        var methodJson = JsonSerializer.Serialize(method);
        var pathJson = JsonSerializer.Serialize(path);
        var bodyJson = body is null ? "null" : JsonSerializer.Serialize(body);
        return $$"""
            (async () => {
              try {
                const sessionRes = await fetch('/api/auth/session', { credentials: 'include' });
                let token = null;
                if (sessionRes.ok) {
                  const session = await sessionRes.json();
                  token = session && session.accessToken ? session.accessToken : null;
                }
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
