namespace CycleArc.Providers.ChatGpt;

public static class ProServerStatusParser
{
    public static bool IsRecognizedProModelSlug(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return false;
        }

        var raw = slug.Trim();
        var lower = raw.ToLowerInvariant();
        if (IsExcludedSlug(lower))
        {
            return false;
        }

        if (lower is "gpt-5-5-pro" or "gpt-5-6-pro" or "gpt-6-pro")
        {
            return true;
        }

        return lower.StartsWith("gpt-", StringComparison.Ordinal)
               && lower.EndsWith("-pro", StringComparison.Ordinal)
               && lower.Length > "gpt-pro".Length;
    }

    public static ProServerStatus Parse(JsonNode? root, DateTimeOffset? observedAt = null)
    {
        if (root is null)
        {
            return ProServerStatus.Unknown();
        }

        var observed = observedAt ?? DateTimeOffset.UtcNow;
        var limits = new List<ProModelLimit>();
        foreach (var item in ChatGptJson.Enumerate(root["model_limits"]))
        {
            var slug = ChatGptJson.GetString(item, "model_slug", "slug", "model", "name");
            if (!IsRecognizedProModelSlug(slug))
            {
                continue;
            }

            limits.Add(new ProModelLimit
            {
                Slug = slug!,
                ResetAt = ReadReset(item),
                Description = SafeText(ChatGptJson.GetString(item, "description"))
            });
        }

        var resets = limits
            .Where(limit => limit.ResetAt is not null)
            .Select(limit => limit.ResetAt!.Value)
            .ToList();
        var (resetAt, resetConfidence, ambiguous) = CorrelateResets(resets);

        string? correlatedName = null;
        int? limitHint = null;
        string? blockReason = null;
        string? description = null;
        var restriction = limits.Count == 0
            ? ProRestrictionState.Unknown
            : ProRestrictionState.NoCorrelatedRestrictionObserved;

        if (limits.Count > 0)
        {
            foreach (var item in ChatGptJson.Enumerate(root["blocked_features"]))
            {
                var featureReset = ReadReset(item);
                if (featureReset is null)
                {
                    continue;
                }

                var matches = resetAt is DateTimeOffset canonical
                    ? WithinTolerance(featureReset.Value, canonical)
                    : resets.Any(reset => WithinTolerance(featureReset.Value, reset));
                if (!matches)
                {
                    continue;
                }

                restriction = ProRestrictionState.CorrelatedRestriction;
                correlatedName = SafeText(ChatGptJson.GetString(item, "name", "feature_name"));
                limitHint = (int?)ChatGptJson.GetDouble(item, "limit");
                blockReason = SafeText(ChatGptJson.GetString(item, "block_reason"));
                description = SafeText(ChatGptJson.GetString(item, "description"));
                break;
            }
        }

        return new ProServerStatus
        {
            ServerObserved = true,
            RestrictionState = restriction,
            ResetAt = resetAt,
            ResetConfidence = resetConfidence,
            ObservedAt = observed,
            ModelLimits = limits,
            CorrelatedBlockedFeatureName = correlatedName,
            CorrelatedBlockedFeatureLimitHint = limitHint,
            BlockReason = blockReason,
            RestrictionDescription = description,
            HasAmbiguousResets = ambiguous,
            LastSuccessfulRefresh = observed,
            LastRefreshAttempt = observed
        };
    }

    public static (DateTimeOffset? ResetAt, ServerResetConfidence Confidence, bool Ambiguous) CorrelateResets(
        IReadOnlyList<DateTimeOffset> resets)
    {
        if (resets.Count == 0)
        {
            return (null, ServerResetConfidence.None, false);
        }

        var min = resets.Min();
        var max = resets.Max();
        if (max - min <= ProServerStatus.ResetCorrelationTolerance)
        {
            return (max, ServerResetConfidence.Server, false);
        }

        return (null, ServerResetConfidence.Ambiguous, true);
    }

    public static bool WithinTolerance(DateTimeOffset left, DateTimeOffset right) =>
        (left > right ? left - right : right - left) <= ProServerStatus.ResetCorrelationTolerance;

    private static DateTimeOffset? ReadReset(JsonNode item) =>
        TimestampParser.ToDateTimeOffset(
            item,
            "resets_after",
            "reset_after",
            "resets_at",
            "reset_at",
            "resetsAt",
            "resetAt");

    private static bool IsExcludedSlug(string lower)
    {
        var compact = new string(lower.Where(char.IsLetterOrDigit).ToArray());
        return compact.Contains("codex", StringComparison.Ordinal)
               || compact.Contains("deepresearch", StringComparison.Ordinal)
               || compact.Contains("image", StringComparison.Ordinal)
               || compact.Contains("voice", StringComparison.Ordinal)
               || compact.Contains("plus", StringComparison.Ordinal)
               || compact.Contains("goplan", StringComparison.Ordinal);
    }

    private static string? SafeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > 280 ? trimmed[..280] : trimmed;
    }
}
