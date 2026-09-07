using CodexMeter.Providers.ChatGpt;

namespace CodexMeter.Tests;

public class ModelNormalizationTests
{
    [Fact]
    public void KnownSolPro_MapsToDisplayName()
    {
        var normalizer = new ModelNormalizer();
        var result = normalizer.Resolve("gpt-5-6-pro", "gpt-5-6-pro");
        Assert.Equal("GPT-5.6 Sol Pro", result.DisplayName);
        Assert.Equal(QuotaFamily.GptPro, result.Family);
        Assert.True(result.mapped);
    }

    [Fact]
    public void UnknownFutureSlug_IsPreserved()
    {
        var normalizer = new ModelNormalizer();
        var result = normalizer.Resolve("gpt-future-omega", "gpt-future-omega");
        Assert.Equal("gpt-future-omega", result.RawSlug);
        Assert.Equal("gpt-future-omega", result.DisplayName);
        Assert.Equal(QuotaFamily.Unknown, result.Family);
        Assert.False(result.mapped);
    }

    [Fact]
    public void CatalogTitle_CanClassifyFutureGpt6Pro()
    {
        var normalizer = new ModelNormalizer();
        normalizer.Observe("gpt-6-astra-pro", "GPT-6 Pro");
        var result = normalizer.Resolve("gpt-6-astra-pro", "gpt-6-astra-pro", "GPT-6 Pro");
        Assert.Equal("GPT-6 Pro", result.DisplayName);
        Assert.Equal(QuotaFamily.GptPro, result.Family);
    }
}
