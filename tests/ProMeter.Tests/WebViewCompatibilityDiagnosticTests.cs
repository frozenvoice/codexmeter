using System.Text.Json;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class WebViewCompatibilityDiagnosticTests
{
    [Fact]
    public async Task Diagnostic_DoesNotMutateSettingsUsageEventsOrWatermarks()
    {
        var directory = NewTempDirectory();
        using var store = new SqliteStore(Path.Combine(directory, "production.db"));
        var existing = Usage("existing", "req-existing", DateTimeOffset.UtcNow.AddMinutes(-5));
        store.UpsertUsageEvents([existing]);
        store.SetState("last_index_sync", "watermark-before");
        var settings = AppSettings.CreateDefaults();
        settings.AuthTransport = AuthTransportKind.BrowserCompanion;
        settings.AutoSync = false;
        settings.StartWithWindows = false;
        var settingsBefore = JsonSerializer.Serialize(settings);
        var eventsBefore = JsonSerializer.Serialize(store.GetUsageEvents());
        var transport = PassingTransport();

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Pass, result.Status);
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(settings));
        Assert.Equal(AuthTransportKind.BrowserCompanion, settings.AuthTransport);
        Assert.False(settings.AutoSync);
        Assert.False(settings.StartWithWindows);
        Assert.Equal(eventsBefore, JsonSerializer.Serialize(store.GetUsageEvents()));
        Assert.Equal("watermark-before", store.GetState("last_index_sync"));
    }

    [Fact]
    public async Task AlreadySignedIn_ProceedsWithoutShowingLogin()
    {
        var transport = PassingTransport();
        var login = new RecordingLogin();

        var result = await new WebViewCompatibilityDiagnostic(transport, login).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Pass, result.Status);
        Assert.Equal(0, login.ShowCount);
    }

    [Fact]
    public async Task SessionAccountModelsAndFirstIndexPage_ReturnPass()
    {
        var transport = PassingTransport();

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.True(result.Passed);
        Assert.Equal(
            [
                ChatGptEndpoints.Session,
                ChatGptEndpoints.AccountsCheck,
                ChatGptEndpoints.Models,
                FirstIndexPath
            ],
            transport.Paths);
    }

    [Fact]
    public async Task UnauthorizedSession_UsesInteractiveLoginThenContinues()
    {
        var transport = PassingTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 401 },
            SignedInSession());
        var login = new RecordingLogin { Result = WebViewInteractiveLoginResult.SignedIn };

        var result = await new WebViewCompatibilityDiagnostic(transport, login).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Pass, result.Status);
        Assert.Equal(1, login.ShowCount);
        Assert.Equal(2, transport.Paths.Count(path => path == ChatGptEndpoints.Session));
    }

    [Fact]
    public async Task SignedOutSession_UsesInteractiveLoginThenContinues()
    {
        var transport = PassingTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 200, Body = "{}" },
            SignedInSession());
        var login = new RecordingLogin { Result = WebViewInteractiveLoginResult.SignedIn };

        var result = await new WebViewCompatibilityDiagnostic(transport, login).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Pass, result.Status);
        Assert.Equal(1, login.ShowCount);
    }

    [Fact]
    public async Task UnsupportedPrimaryAccountProbe_UsesSafeMeFallback()
    {
        var transport = PassingTransport();
        transport.SetQueue(ChatGptEndpoints.AccountsCheck, new ProviderResponse { Status = 404 });
        transport.SetQueue(ChatGptEndpoints.Me, Ok("{\"id\":\"synthetic-user\"}"));

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Pass, result.Status);
        Assert.Contains(ChatGptEndpoints.Me, transport.Paths);
    }

    [Fact]
    public async Task CancelledInteractiveLogin_PreservesAllSettings()
    {
        var settings = AppSettings.CreateDefaults();
        settings.AuthTransport = AuthTransportKind.BrowserCompanion;
        settings.ChromeExtensionId = "abcdefghijklmnopqrstuvwxzyabcdef";
        settings.AutoSync = true;
        settings.StartWithWindows = true;
        var before = JsonSerializer.Serialize(settings);
        var transport = PassingTransport();
        transport.SetQueue(ChatGptEndpoints.Session, new ProviderResponse { Status = 401 });
        var login = new RecordingLogin { Result = WebViewInteractiveLoginResult.Cancelled };

        var result = await new WebViewCompatibilityDiagnostic(transport, login).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Cancelled, result.Status);
        Assert.Equal(before, JsonSerializer.Serialize(settings));
        Assert.Equal(1, login.ShowCount);
        Assert.Single(transport.Paths);
    }

    [Fact]
    public async Task UnsupportedInteractiveLogin_ReturnsFailAuth()
    {
        var transport = PassingTransport();
        transport.SetQueue(ChatGptEndpoints.Session, new ProviderResponse { Status = 401 });
        var login = new RecordingLogin { Result = WebViewInteractiveLoginResult.Unsupported };

        var result = await new WebViewCompatibilityDiagnostic(transport, login).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.FailAuth, result.Status);
    }

    [Fact]
    public async Task SuccessfulLoginButInvalidSubsequentSession_ReturnsFailSession()
    {
        var transport = PassingTransport();
        transport.SetQueue(
            ChatGptEndpoints.Session,
            new ProviderResponse { Status = 401 },
            new ProviderResponse { Status = 200, Body = "{}" });

        var result = await new WebViewCompatibilityDiagnostic(
            transport,
            new RecordingLogin { Result = WebViewInteractiveLoginResult.SignedIn }).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.FailSession, result.Status);
        Assert.Equal(200, result.HttpStatus);
    }

    [Fact]
    public async Task Forbidden_IsDistinctFromAuthenticationFailure()
    {
        var transport = PassingTransport();
        transport.SetQueue(ChatGptEndpoints.Session, new ProviderResponse { Status = 403 });

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.Forbidden, result.Status);
        Assert.Equal(403, result.HttpStatus);
        Assert.NotEqual(WebViewDiagnosticStatus.FailAuth, result.Status);
    }

    [Fact]
    public async Task SchemaMismatch_ReturnsFailApi()
    {
        var transport = PassingTransport();
        transport.SetQueue(
            ChatGptEndpoints.AccountsCheck,
            new ProviderResponse { Status = 200, SchemaMismatch = true, Body = "{}" });

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
        Assert.Contains("schema mismatch", result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ModelsFailure_ReturnsFailApi()
    {
        var transport = PassingTransport();
        transport.SetQueue(ChatGptEndpoints.Models, new ProviderResponse { Status = 500 });

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
        Assert.Equal(500, result.HttpStatus);
        Assert.Contains("models", result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstIndexPageFailure_ReturnsFailApi()
    {
        var transport = PassingTransport();
        transport.SetQueue(FirstIndexPath, new ProviderResponse { Status = 502 });

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.Equal(WebViewDiagnosticStatus.FailApi, result.Status);
        Assert.Equal(502, result.HttpStatus);
        Assert.Contains("conversation index", result.TechnicalDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LightweightTest_NeverCallsPaginationProjectsArchivesOrBodies()
    {
        var transport = PassingTransport();

        await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();

        Assert.Equal(4, transport.Paths.Count);
        Assert.DoesNotContain(transport.Paths, path => path.Contains("is_archived=true", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Paths, path => path.Contains("gizmos", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Paths, path => path.Contains("include_full_conversation", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Paths, path => path.Contains("/messages", StringComparison.Ordinal));
        Assert.DoesNotContain(transport.Paths, path => path.Contains("num_turns", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Result_DoesNotExposeBodiesTokensCookiesOrAuthorizationValues()
    {
        const string secret = "diagnostic-secret-value";
        var transport = PassingTransport();
        transport.SetQueue(ChatGptEndpoints.Models, new ProviderResponse
        {
            Status = 500,
            Error = $"Authorization: Bearer {secret}; Cookie={secret}",
            Body = $"{{\"prompt\":\"{secret}\",\"assistant\":\"{secret}\"}}"
        });

        var result = await new WebViewCompatibilityDiagnostic(transport, new RecordingLogin()).RunAsync();
        var serialized = JsonSerializer.Serialize(result);

        Assert.DoesNotContain(secret, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("prompt", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("assistant", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie", serialized, StringComparison.OrdinalIgnoreCase);
    }

    private static ScriptedTransport PassingTransport()
    {
        var transport = new ScriptedTransport();
        transport.SetQueue(ChatGptEndpoints.Session, SignedInSession());
        transport.SetQueue(ChatGptEndpoints.AccountsCheck, Ok("{\"accounts\":{}}"));
        transport.SetQueue(ChatGptEndpoints.Models, Ok("{\"models\":[]}"));
        transport.SetQueue(FirstIndexPath, Ok("{\"items\":[],\"total\":0,\"offset\":0,\"limit\":100,\"has_more\":false}"));
        return transport;
    }

    private static ProviderResponse SignedInSession() =>
        Ok("{\"user\":{\"id\":\"synthetic-user\"}}");

    private static ProviderResponse Ok(string body) => new() { Status = 200, Body = body };

    private static UsageEvent Usage(string conversationId, string requestId, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ConversationId = conversationId,
        RequestId = requestId,
        MessageId = "message-" + conversationId,
        CreatedAt = createdAt,
        FirstSeenAt = createdAt,
        LastSeenAt = createdAt,
        RawModel = "gpt-5-6-pro",
        NormalizedModel = "GPT-5.6 Sol Pro",
        QuotaFamily = QuotaFamily.GptPro,
        DedupeKey = "req:" + requestId
    };

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string FirstIndexPath =>
        ChatGptEndpoints.ConversationsPage(0, ConversationIndexPager.RequestedLimit, archived: false);

    private sealed class RecordingLogin : IWebViewInteractiveLogin
    {
        public int ShowCount { get; private set; }
        public WebViewInteractiveLoginResult Result { get; set; } = WebViewInteractiveLoginResult.SignedIn;

        public Task<WebViewInteractiveLoginResult> ShowInteractiveLoginAsync(CancellationToken cancellationToken = default)
        {
            ShowCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class ScriptedTransport : IChatGptTransport
    {
        private readonly Dictionary<string, Queue<ProviderResponse>> _responses = new(StringComparer.Ordinal);
        public List<string> Paths { get; } = [];

        public void SetQueue(string path, params ProviderResponse[] responses) =>
            _responses[path] = new Queue<ProviderResponse>(responses);

        public Task<ProviderResponse> SendAsync(
            string method,
            string path,
            string? jsonBody = null,
            CancellationToken cancellationToken = default)
        {
            _ = method;
            _ = jsonBody;
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(path);
            if (_responses.TryGetValue(path, out var queue) && queue.Count > 0)
            {
                return Task.FromResult(queue.Dequeue());
            }

            return Task.FromResult(new ProviderResponse { Status = 500, Error = "unexpected diagnostic operation" });
        }
    }
}
