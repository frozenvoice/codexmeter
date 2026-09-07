using System.Text.Json;
using CodexMeter.Providers.ChatGpt;
using CodexMeter.Services;

namespace CodexMeter.Tests;

public class SessionAuthCoordinatorTests
{
    private const string Token = "test-access-token-value-aa11";

    [Fact]
    public async Task OneSessionFetch_ServesMultipleBackendRequests()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.On(ChatGptEndpoints.Session, SessionOk());
        fetch.On("/backend-api/conversations", Ok("""{"items":[]}"""));
        fetch.On("/backend-api/models", Ok("""{"models":[]}"""));

        await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/conversations", null, CancellationToken.None);
        await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/models", null, CancellationToken.None);

        Assert.Equal(1, coordinator.SessionFetchCount);
        Assert.Equal(1, fetch.Count(ChatGptEndpoints.Session));
        Assert.Equal(1, fetch.Count("/backend-api/conversations"));
        Assert.Equal(1, fetch.Count("/backend-api/models"));
        Assert.True(coordinator.HasCachedSession);
    }

    [Fact]
    public async Task SessionEndpoint_IsNotPrefetched()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.On(ChatGptEndpoints.Session, SessionOk());

        await coordinator.SendAsync(fetch.Send, "GET", ChatGptEndpoints.Session, null, CancellationToken.None);

        Assert.Equal(1, coordinator.SessionFetchCount);
        Assert.Single(fetch.Paths);
        Assert.Equal(ChatGptEndpoints.Session, fetch.Paths[0]);
    }

    [Fact]
    public async Task First401_RefreshesOnceAndRetriesOnce()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.On(ChatGptEndpoints.Session, SessionOk());
        var backendHits = 0;
        fetch.Handler["/backend-api/me"] = (_, _, token) =>
        {
            backendHits++;
            return backendHits == 1
                ? new ProviderResponse { Status = 401, Error = "expired" }
                : Ok("""{"email":"a@b.com"}""");
        };

        var response = await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/me", null, CancellationToken.None);
        Assert.True(response.IsSuccess);
        Assert.Equal(2, coordinator.SessionFetchCount);
        Assert.Equal(2, backendHits);
    }

    [Fact]
    public async Task Second401_IsReturnedToCaller()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.On(ChatGptEndpoints.Session, SessionOk());
        fetch.On("/backend-api/me", new ProviderResponse { Status = 401, Error = "still expired" });

        var response = await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/me", null, CancellationToken.None);
        Assert.True(response.IsUnauthorized);
        Assert.Equal(401, response.Status);
        Assert.Equal(2, coordinator.SessionFetchCount);
        Assert.Equal(2, fetch.Count("/backend-api/me"));
    }

    [Fact]
    public async Task Refresh429_IsReturnedInsteadOfOriginal401()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.SessionQueue.Enqueue(SessionOk());
        fetch.SessionQueue.Enqueue(new ProviderResponse { Status = 429, Error = "rate limited" });
        fetch.On("/backend-api/me", new ProviderResponse { Status = 401, Error = "expired" });

        var response = await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/me", null, CancellationToken.None);
        Assert.True(response.IsRateLimited);
        Assert.Equal(429, response.Status);
        Assert.Equal(1, fetch.Count("/backend-api/me"));
    }

    [Fact]
    public async Task RefreshOffline_IsReturnedInsteadOfOriginal401()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.SessionQueue.Enqueue(SessionOk());
        fetch.SessionQueue.Enqueue(new ProviderResponse { Status = 0, Error = "offline" });
        fetch.On("/backend-api/me", new ProviderResponse { Status = 401, Error = "expired" });

        var response = await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/me", null, CancellationToken.None);
        Assert.True(response.IsOffline);
        Assert.Equal(0, response.Status);
        Assert.False(response.SchemaMismatch);
    }

    [Fact]
    public async Task RefreshSchemaMismatch_RemainsSchemaMismatch()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.SessionQueue.Enqueue(SessionOk());
        fetch.SessionQueue.Enqueue(new ProviderResponse { Status = 0, SchemaMismatch = true, Error = "session shape changed" });
        fetch.On("/backend-api/me", new ProviderResponse { Status = 401, Error = "expired" });

        var response = await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/me", null, CancellationToken.None);
        Assert.True(response.SchemaMismatch);
        Assert.False(response.IsOffline);
        Assert.False(response.IsUnauthorized);
    }

    [Fact]
    public async Task Refresh500_IsReturnedInsteadOfOriginal401()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch();
        fetch.SessionQueue.Enqueue(SessionOk());
        fetch.SessionQueue.Enqueue(new ProviderResponse { Status = 500, Error = "upstream" });
        fetch.On("/backend-api/me", new ProviderResponse { Status = 401, Error = "expired" });

        var response = await coordinator.SendAsync(fetch.Send, "GET", "/backend-api/me", null, CancellationToken.None);
        Assert.True(response.IsServerError);
        Assert.Equal(500, response.Status);
    }

    [Fact]
    public async Task ConcurrentBackendRequests_ShareOneSessionFetch()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch { Delay = TimeSpan.FromMilliseconds(200) };
        fetch.On(ChatGptEndpoints.Session, SessionOk());
        fetch.On("/backend-api/conversations", Ok("""{"items":[]}"""));
        fetch.On("/backend-api/models", Ok("""{"models":[]}"""));

        await Task.WhenAll(
            coordinator.SendAsync(fetch.Send, "GET", "/backend-api/conversations", null, CancellationToken.None),
            coordinator.SendAsync(fetch.Send, "GET", "/backend-api/models", null, CancellationToken.None));

        Assert.Equal(1, coordinator.SessionFetchCount);
        Assert.Equal(1, fetch.Count(ChatGptEndpoints.Session));
    }

    [Fact]
    public async Task Concurrent401_ShareOneRefresh()
    {
        var coordinator = new SessionAuthCoordinator();
        coordinator.ApplySession(ChatGptJson.ParseNode(SessionOk().Body), DateTimeOffset.UtcNow.AddHours(1));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fetch = new RecordingFetch { BlockSessionUntil = gate.Task };
        fetch.On(ChatGptEndpoints.Session, SessionOk());
        fetch.On("/backend-api/me", new ProviderResponse { Status = 401, Error = "expired" });
        fetch.On("/backend-api/models", new ProviderResponse { Status = 401, Error = "expired" });

        var first = coordinator.SendAsync(fetch.Send, "GET", "/backend-api/me", null, CancellationToken.None);
        var second = coordinator.SendAsync(fetch.Send, "GET", "/backend-api/models", null, CancellationToken.None);
        for (var i = 0; i < 50 && fetch.Count("/backend-api/me") + fetch.Count("/backend-api/models") < 2; i++)
        {
            await Task.Delay(10);
        }

        gate.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, coordinator.SessionFetchCount);
        Assert.Equal(1, fetch.Count(ChatGptEndpoints.Session));
    }

    [Fact]
    public async Task CancellingOneCaller_DoesNotCorruptSharedSession()
    {
        var coordinator = new SessionAuthCoordinator();
        var fetch = new RecordingFetch { Delay = TimeSpan.FromMilliseconds(250) };
        fetch.On(ChatGptEndpoints.Session, SessionOk());
        fetch.On("/backend-api/conversations", Ok("""{"items":[]}"""));
        fetch.On("/backend-api/models", Ok("""{"models":[]}"""));
        using var cts = new CancellationTokenSource();

        var cancelled = coordinator.SendAsync(fetch.Send, "GET", "/backend-api/conversations", null, cts.Token);
        var kept = coordinator.SendAsync(fetch.Send, "GET", "/backend-api/models", null, CancellationToken.None);
        await Task.Delay(40);
        cts.Cancel();

        var keptResponse = await kept;
        Assert.True(keptResponse.IsSuccess);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True(coordinator.HasCachedSession);
        Assert.Equal(1, coordinator.SessionFetchCount);
    }

    [Fact]
    public void Token_NeverAppearsInLogsOrSerializedSettings()
    {
        var coordinator = new SessionAuthCoordinator();
        coordinator.ApplySession(new JsonObject
        {
            ["accessToken"] = Token,
            ["expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
        }, DateTimeOffset.UtcNow);

        var dir = Path.Combine(Path.GetTempPath(), "codexmeter-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = new AppLog(dir);
        log.Info("accessToken=" + Token);
        log.Info("Authorization: Bearer " + Token);
        var logged = File.ReadAllText(Directory.GetFiles(dir, "*.log")[0]);
        Assert.DoesNotContain(Token, logged);

        var settingsJson = JsonSerializer.Serialize(AppSettings.CreateDefaults());
        Assert.DoesNotContain(Token, settingsJson);
        Assert.DoesNotContain("accessToken", settingsJson, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.HasCachedSession);
    }

    private static ProviderResponse SessionOk() =>
        Ok("{\"accessToken\":\"" + Token + "\",\"user\":{\"email\":\"a@b.com\",\"id\":\"u1\"}}");

    private static ProviderResponse Ok(string body) => new() { Status = 200, Body = body };

    private sealed class RecordingFetch
    {
        public List<string> Paths { get; } = [];
        public Dictionary<string, Func<string, string?, string?, ProviderResponse>> Handler { get; } = new(StringComparer.Ordinal);
        public Queue<ProviderResponse> SessionQueue { get; } = new();
        public TimeSpan Delay { get; set; }
        public Task? BlockSessionUntil { get; set; }

        public void On(string path, ProviderResponse response) =>
            Handler[path] = (_, _, _) => response;

        public int Count(string path) => Paths.Count(item => item == path);

        private readonly object _gate = new();

        public async Task<ProviderResponse> Send(string method, string path, string? body, string? token, CancellationToken cancellationToken)
        {
            _ = method;
            _ = body;
            _ = token;
            _ = cancellationToken;
            lock (_gate)
            {
                Paths.Add(path);
            }

            if (SessionAuthCoordinator.IsSessionPath(path) && BlockSessionUntil is not null)
            {
                await BlockSessionUntil;
            }

            if (SessionAuthCoordinator.IsSessionPath(path) && Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, CancellationToken.None);
            }

            if (SessionAuthCoordinator.IsSessionPath(path))
            {
                lock (_gate)
                {
                    if (SessionQueue.Count > 0)
                    {
                        return SessionQueue.Dequeue();
                    }
                }
            }

            if (Handler.TryGetValue(path, out var handler))
            {
                return handler(path, body, token);
            }

            return new ProviderResponse { Status = 500, Error = "unexpected " + path };
        }
    }
}
