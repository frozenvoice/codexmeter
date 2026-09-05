using ProMeter.Services;

namespace ProMeter.Tests;

public class ProServerStatusStoreTests
{
    [Fact]
    public void PersistsSafeMetadataOnly()
    {
        var path = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"), "status.json");
        var store = new ProServerStatusStore(path);
        store.Save(new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = ProRestrictionState.CorrelatedRestriction,
            ResetAt = new DateTimeOffset(2026, 9, 6, 5, 20, 13, TimeSpan.Zero),
            ResetConfidence = ServerResetConfidence.Server,
            CorrelatedBlockedFeatureName = "reason",
            CorrelatedBlockedFeatureLimitHint = 50,
            RestrictionDescription = "server supplied restriction notice",
            ModelLimits = [new ProModelLimit { Slug = "gpt-6-pro", ResetAt = new DateTimeOffset(2026, 9, 6, 5, 20, 13, TimeSpan.Zero) }]
        });

        var json = File.ReadAllText(path);
        Assert.False(ProServerStatusStore.ContainsForbiddenPayload(json));
        Assert.DoesNotContain("access_token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("conversation_id", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gpt-6-pro", json, StringComparison.Ordinal);
        var loaded = store.Load();
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, loaded?.RestrictionState);
        Assert.Equal(50, loaded?.CorrelatedBlockedFeatureLimitHint);
    }

    [Fact]
    public void RejectsSecretPayload()
    {
        Assert.True(ProServerStatusStore.ContainsForbiddenPayload("{\"email\":\"user@example.invalid\"}"));
        Assert.True(ProServerStatusStore.ContainsForbiddenPayload("{\"access_token\":\"secret\"}"));
    }
}
