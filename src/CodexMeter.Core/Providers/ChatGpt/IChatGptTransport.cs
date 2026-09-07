namespace CodexMeter.Providers.ChatGpt;

public interface IChatGptTransport
{
    Task<ProviderResponse> SendAsync(string method, string path, string? jsonBody = null, CancellationToken cancellationToken = default);
}
