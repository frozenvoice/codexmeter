using System.Text.Json;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

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
    public void Token_NeverAppearsInLogsOrSerializedSettings()
    {
        var coordinator = new SessionAuthCoordinator();
        coordinator.ApplySession(new JsonObject
        {
            ["accessToken"] = Token,
            ["expires"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
        }, DateTimeOffset.UtcNow);

        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
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

        public void On(string path, ProviderResponse response) =>
            Handler[path] = (_, _, _) => response;

        public int Count(string path) => Paths.Count(item => item == path);

        public Task<ProviderResponse> Send(string method, string path, string? body, string? token, CancellationToken cancellationToken)
        {
            _ = method;
            _ = body;
            _ = cancellationToken;
            Paths.Add(path);
            if (Handler.TryGetValue(path, out var handler))
            {
                return Task.FromResult(handler(path, body, token));
            }

            return Task.FromResult(new ProviderResponse { Status = 500, Error = "unexpected " + path });
        }
    }
}
