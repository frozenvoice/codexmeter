using ProMeter.Providers.ChatGpt;

namespace ProMeter.Tests;

public class TransportSchemaTests
{
    [Fact]
    public async Task EmptyTransportEnvelope_IsSchemaMismatchNotOffline()
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 0,
            Error = "empty transport result",
            SchemaMismatch = true
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(
            () => new ChatGptProvider(transport).GetConversationIndexAsync(false));
        Assert.True(ex.SchemaMismatch);
        Assert.False(ex.IsOffline);
    }

    [Fact]
    public async Task NetworkFailure_RemainsOffline()
    {
        var transport = new PaginationTests.ScriptedTransport((_, _) => new ProviderResponse
        {
            Status = 0,
            Error = "offline"
        });
        var ex = await Assert.ThrowsAsync<ChatGptProviderException>(
            () => new ChatGptProvider(transport).GetConversationIndexAsync(false));
        Assert.True(ex.IsOffline);
        Assert.False(ex.SchemaMismatch);
    }
}
