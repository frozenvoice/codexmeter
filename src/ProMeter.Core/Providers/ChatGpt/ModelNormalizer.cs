namespace ProMeter.Providers.ChatGpt;

public sealed class ModelNormalizer
{
    private readonly Dictionary<string, MappedModel> _confirmed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MappedModel> _observed = new(StringComparer.OrdinalIgnoreCase);

    public ModelNormalizer()
    {
        // Confirmed ChatGPT web slugs observed in 2026 account metadata.
        // Do not invent GPT-6 internal slugs here.
        RegisterConfirmed("gpt-5-6-pro", "GPT-5.6 Sol Pro", QuotaFamily.GptPro);
        RegisterConfirmed("gpt-5.6-pro", "GPT-5.6 Sol Pro", QuotaFamily.GptPro);
        RegisterConfirmed("gpt-5-6-sol-pro", "GPT-5.6 Sol Pro", QuotaFamily.GptPro);

        RegisterConfirmed("gpt-5-6", "GPT-5.6 Sol", QuotaFamily.Instant);
        RegisterConfirmed("gpt-5.6", "GPT-5.6 Sol", QuotaFamily.Instant);
        RegisterConfirmed("gpt-5-6-instant", "GPT-5.6 Sol", QuotaFamily.Instant);
        RegisterConfirmed("gpt-5.6-instant", "GPT-5.6 Sol", QuotaFamily.Instant);

        RegisterConfirmed("gpt-5-6-thinking", "GPT-5.6 Sol", QuotaFamily.SolReasoning);
        RegisterConfirmed("gpt-5.6-thinking", "GPT-5.6 Sol", QuotaFamily.SolReasoning);
        RegisterConfirmed("gpt-5-6-sol", "GPT-5.6 Sol", QuotaFamily.SolReasoning);
    }

    public IReadOnlyDictionary<string, MappedModel> ConfirmedMappings => _confirmed;

    public void ObserveCatalog(IEnumerable<ModelCatalogEntry> catalog)
    {
        foreach (var entry in catalog)
        {
            Observe(entry.Slug, entry.Title, entry.Description);
        }
    }

    public void Observe(string? slug, string? title, string? description = null)
    {
        var key = NormalizeSlug(slug);
        if (string.IsNullOrWhiteSpace(key) || _confirmed.ContainsKey(key))
        {
            return;
        }

        var family = ClassifyFromSignals(key, title, description);
        var display = !string.IsNullOrWhiteSpace(title) ? title.Trim() : key;
        _observed[key] = new MappedModel(key, display, family, Mapped: family != QuotaFamily.Unknown);
    }

    public NormalizedModel Resolve(string? requestedModel, string? responseModel, string? catalogTitle = null)
    {
        var raw = FirstSlug(responseModel, requestedModel);
        var requested = NormalizeSlug(requestedModel);
        var response = NormalizeSlug(responseModel);
        var key = FirstSlug(response, requested);

        if (string.IsNullOrWhiteSpace(key))
        {
            return new NormalizedModel("", "Unknown", QuotaFamily.Unknown, mapped: false);
        }

        if (_confirmed.TryGetValue(key, out var confirmed))
        {
            return ToNormalized(confirmed, raw);
        }

        if (!string.IsNullOrWhiteSpace(response) && _confirmed.TryGetValue(response, out confirmed))
        {
            return ToNormalized(confirmed, raw);
        }

        if (!string.IsNullOrWhiteSpace(requested) && _confirmed.TryGetValue(requested, out confirmed))
        {
            return ToNormalized(confirmed, raw);
        }

        Observe(key, catalogTitle);
        if (_observed.TryGetValue(key, out var observed) && observed.Mapped)
        {
            return ToNormalized(observed, raw);
        }

        var family = ClassifyFromSignals(key, catalogTitle, null);
        var display = !string.IsNullOrWhiteSpace(catalogTitle)
            ? catalogTitle.Trim()
            : key;
        return new NormalizedModel(raw.Length > 0 ? raw : key, display, family, mapped: family != QuotaFamily.Unknown);
    }

    public static string NormalizeSlug(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return value.Trim().ToLowerInvariant()
            .Replace('_', '-')
            .Replace('\u2010', '-')
            .Replace('\u2011', '-')
            .Replace('\u2012', '-')
            .Replace('\u2013', '-')
            .Replace('\u2014', '-')
            .Replace('\u2015', '-');
    }

    private void RegisterConfirmed(string slug, string display, QuotaFamily family)
    {
        var key = NormalizeSlug(slug);
        _confirmed[key] = new MappedModel(key, display, family, Mapped: true);
    }

    private static NormalizedModel ToNormalized(MappedModel mapped, string raw) =>
        new(string.IsNullOrWhiteSpace(raw) ? mapped.Slug : raw, mapped.DisplayName, mapped.Family, mapped.Mapped);

    private static string FirstSlug(params string?[] values)
    {
        foreach (var value in values)
        {
            var slug = NormalizeSlug(value);
            if (!string.IsNullOrWhiteSpace(slug))
            {
                return slug;
            }
        }

        return "";
    }

    public static QuotaFamily ClassifyFromSignals(string slug, string? title, string? description)
    {
        var text = string.Join(" ", new[] { slug, title, description }.Where(s => !string.IsNullOrWhiteSpace(s)))
            .ToLowerInvariant();

        if (LooksLikeGptPro(slug, text))
        {
            return QuotaFamily.GptPro;
        }

        if (LooksLikeSolReasoning(slug, text))
        {
            return QuotaFamily.SolReasoning;
        }

        if (LooksLikeInstant(slug, text))
        {
            return QuotaFamily.Instant;
        }

        return QuotaFamily.Unknown;
    }

    private static bool LooksLikeGptPro(string slug, string text)
    {
        if (slug is "gpt-5-6-pro" or "gpt-5.6-pro" or "gpt-5-6-sol-pro")
        {
            return true;
        }

        var titleLooksPro = text.Contains("gpt-6 pro", StringComparison.Ordinal)
            || text.Contains("gpt 6 pro", StringComparison.Ordinal)
            || text.Contains("gpt-5.6 sol pro", StringComparison.Ordinal)
            || text.Contains("gpt-5.6 pro", StringComparison.Ordinal)
            || text.Contains("sol pro", StringComparison.Ordinal);

        if (titleLooksPro)
        {
            return true;
        }

        // Accept a future GPT-6 Pro slug only when the slug itself is explicitly Pro.
        // Do not map unknown gpt-6* slugs to Pro just because they mention 6.
        return slug.StartsWith("gpt-6", StringComparison.OrdinalIgnoreCase)
            && slug.EndsWith("-pro", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSolReasoning(string slug, string text)
    {
        if (slug.Contains("thinking", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return text.Contains("reasoning", StringComparison.Ordinal)
            && (text.Contains("5.6", StringComparison.Ordinal) || text.Contains("5-6", StringComparison.Ordinal) || text.Contains("sol", StringComparison.Ordinal));
    }

    private static bool LooksLikeInstant(string slug, string text)
    {
        return slug.Contains("instant", StringComparison.OrdinalIgnoreCase)
            || text.Contains("instant", StringComparison.Ordinal);
    }

    public sealed record MappedModel(string Slug, string DisplayName, QuotaFamily Family, bool Mapped);
}

public sealed record NormalizedModel(string RawSlug, string DisplayName, QuotaFamily Family, bool mapped);
