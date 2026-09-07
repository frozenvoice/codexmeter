using CodexMeter.Services;

namespace CodexMeter.Providers.ChatGpt;

public static class RequestCanonicalizer
{
    public static IReadOnlyList<UsageEvent> Canonicalize(
        IReadOnlyList<UsageObservation> observations,
        ModelNormalizer models,
        ConversationParseContext context,
        IClock clock)
    {
        var byId = new Dictionary<string, UsageObservation>(StringComparer.OrdinalIgnoreCase);
        foreach (var observation in observations)
        {
            if (!string.IsNullOrWhiteSpace(observation.MessageId))
            {
                byId[observation.MessageId] = observation;
            }
        }

        var assistants = observations.Where(IsAssistantLike).ToList();
        var groups = Group(context.ConversationId, assistants, byId);
        var now = clock.UtcNow;
        var events = new List<UsageEvent>(groups.Count);
        foreach (var (key, group) in groups)
        {
            var usage = BuildEvent(key, group, byId, models, context, now);
            if (usage is not null)
            {
                events.Add(usage);
            }
        }

        return events;
    }

    public static Dictionary<string, List<UsageObservation>> Group(
        string conversationId,
        IReadOnlyList<UsageObservation> assistants,
        IReadOnlyDictionary<string, UsageObservation> nodes)
    {
        var groups = new Dictionary<string, List<UsageObservation>>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in assistants.Where(n => !string.IsNullOrWhiteSpace(n.RequestId)))
        {
            AddGroup(groups, UsageEvent.ScopedRequestKey(conversationId, node.RequestId!), node);
            assigned.Add(node.MessageId ?? node.Id);
        }

        foreach (var node in assistants.Where(n => !assigned.Contains(n.MessageId ?? n.Id)))
        {
            var tagged = FindLinkedRequestGroup(groups, nodes, node);
            if (tagged is null)
            {
                continue;
            }

            AddGroup(groups, tagged, node);
            assigned.Add(node.MessageId ?? node.Id);
        }

        var remaining = assistants.Where(n => !assigned.Contains(n.MessageId ?? n.Id)).ToList();
        foreach (var family in remaining.GroupBy(n => FindUserAncestor(nodes, n) ?? n.ParentMessageId ?? n.MessageId ?? n.Id))
        {
            var members = family.ToList();
            var finals = members.Where(IsVisibleFinal).OrderBy(n => n.CreatedAt).ToList();
            var fragments = members.Where(n => !IsVisibleFinal(n)).ToList();
            if (finals.Count <= 1)
            {
                var key = "turn:" + conversationId + ":" + family.Key;
                foreach (var node in members)
                {
                    AddGroup(groups, key, node);
                }

                continue;
            }

            foreach (var final in finals)
            {
                AddGroup(groups, "turn:" + conversationId + ":" + family.Key + ":final:" + (final.MessageId ?? final.Id), final);
            }

            foreach (var fragment in fragments)
            {
                var linked = finals.FirstOrDefault(final => CompatibleBranch(nodes, fragment, final));
                if (linked is null)
                {
                    AddGroup(groups, "unresolved:" + conversationId + ":" + (fragment.MessageId ?? fragment.Id), fragment);
                    continue;
                }

                AddGroup(groups, "turn:" + conversationId + ":" + family.Key + ":final:" + (linked.MessageId ?? linked.Id), fragment);
            }
        }

        return groups;
    }

    public static IReadOnlyList<UsageEvent> MergeCanonical(
        IReadOnlyList<UsageEvent> existing,
        IReadOnlyList<UsageEvent> incoming)
    {
        var merged = new Dictionary<string, UsageEvent>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Remember(UsageEvent usage)
        {
            merged[usage.DedupeKey] = usage;
            aliases[usage.DedupeKey] = usage.DedupeKey;
            if (!string.IsNullOrWhiteSpace(usage.RequestId))
            {
                aliases[UsageEvent.ScopedRequestKey(usage.ConversationId, usage.RequestId)] = usage.DedupeKey;
                aliases["req:" + usage.RequestId] = usage.DedupeKey;
            }

            if (!string.IsNullOrWhiteSpace(usage.MessageId))
            {
                aliases["msg:" + usage.ConversationId + ":" + usage.MessageId] = usage.DedupeKey;
            }

            foreach (var alias in SplitAliases(usage.IdentityAliases))
            {
                aliases[alias] = usage.DedupeKey;
            }
        }

        foreach (var usage in existing)
        {
            Remember(Clone(usage));
        }

        foreach (var usage in incoming)
        {
            var match = ResolveAlias(aliases, usage);
            if (match is not null && merged.TryGetValue(match, out var prior))
            {
                var combined = Combine(prior, usage);
                if (!string.Equals(prior.DedupeKey, combined.DedupeKey, StringComparison.OrdinalIgnoreCase))
                {
                    merged.Remove(prior.DedupeKey);
                }

                Remember(combined);
                continue;
            }

            Remember(Clone(usage));
        }

        return merged.Values.OrderBy(e => e.CreatedAt).ToList();
    }

    /// <summary>
    /// Result of replacing derived rows with a ledger rebuild. Observations are never destroyed;
    /// only derived canonical rows are replaced, and evidence that the ledger cannot reproduce
    /// (official exports) is preserved and reconciled by alias.
    /// </summary>
    public sealed record LedgerRebuild(
        IReadOnlyList<UsageEvent> Events,
        int MergedDuplicateCorrections);

    /// <summary>
    /// Replaces the derived canonical rows of one conversation with <paramref name="rebuilt"/>,
    /// carrying first-seen evidence forward and preserving sources the observation ledger
    /// cannot reconstruct.
    /// </summary>
    public static LedgerRebuild ApplyRebuild(
        IReadOnlyList<UsageEvent> existing,
        IReadOnlyList<UsageEvent> rebuilt)
    {
        var byAlias = new Dictionary<string, UsageEvent>(StringComparer.OrdinalIgnoreCase);
        foreach (var usage in existing)
        {
            foreach (var alias in CandidateKeys(usage))
            {
                byAlias.TryAdd(alias, usage);
            }
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merges = 0;
        var results = new List<UsageEvent>(rebuilt.Count);
        foreach (var usage in rebuilt)
        {
            var priors = new List<UsageEvent>();
            foreach (var alias in CandidateKeys(usage))
            {
                if (byAlias.TryGetValue(alias, out var prior) && !priors.Contains(prior))
                {
                    priors.Add(prior);
                }
            }

            var carried = Clone(usage);
            if (priors.Count > 0)
            {
                carried.FirstSeenAt = priors.Min(p => p.FirstSeenAt) is var first && first != default
                    ? (first <= carried.FirstSeenAt ? first : carried.FirstSeenAt)
                    : carried.FirstSeenAt;
                foreach (var prior in priors)
                {
                    matched.Add(prior.DedupeKey);
                }

                if (priors.Count > 1)
                {
                    merges += priors.Count - 1;
                    carried.CorrectionReason ??= "merged_duplicate";
                }
                else if (!string.Equals(priors[0].DedupeKey, carried.DedupeKey, StringComparison.OrdinalIgnoreCase))
                {
                    carried.CorrectionReason ??= "identity_merge";
                }
            }

            results.Add(carried);
        }

        var unreproducible = existing
            .Where(e => e.Source == UsageSource.OfficialExport && !matched.Contains(e.DedupeKey))
            .ToList();
        if (unreproducible.Count == 0)
        {
            return new LedgerRebuild(results.OrderBy(e => e.CreatedAt).ToList(), merges);
        }

        return new LedgerRebuild(MergeCanonical(unreproducible, results), merges);
    }

    private static UsageEvent? BuildEvent(
        string key,
        List<UsageObservation> group,
        IReadOnlyDictionary<string, UsageObservation> nodes,
        ModelNormalizer models,
        ConversationParseContext context,
        DateTimeOffset now)
    {
        var representative = SelectRepresentative(group);
        if (representative is null)
        {
            return null;
        }

        var requested = FirstNonEmpty(group.Select(n => n.RequestedModel)
            .Append(FindParentRequestedModel(nodes, representative))
            .Append(context.DefaultModelSlug));
        var responseSlugs = group
            .Select(n => FirstNonEmpty(n.ResponseModel, n.RawModel))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var response = responseSlugs.Count == 1 ? responseSlugs[0] : FirstNonEmpty(representative.ResponseModel, representative.RawModel);
        var normalized = models.Resolve(requested, responseSlugs.Count <= 1 ? response : null);
        if (responseSlugs.Count > 1)
        {
            var families = responseSlugs
                .Select(slug => models.Resolve(requested, slug).Family)
                .Distinct()
                .ToList();
            if (families.Count > 1)
            {
                normalized = new NormalizedModel(
                    FirstNonEmpty(responseSlugs) ?? "",
                    "Unknown",
                    QuotaFamily.Unknown,
                    mapped: false);
            }
            else if (families.Count == 1)
            {
                normalized = models.Resolve(requested, responseSlugs[0]);
            }
        }

        var effort = group.Select(n => n.Effort).FirstOrDefault(e => e != ReasoningEffort.Unknown);
        var family = RefineFamily(normalized.Family, effort, normalized.RawSlug);
        var modelConfidence = ModelConfidence(requested, responseSlugs, family);
        if (modelConfidence == ModelEvidenceConfidence.RequestedOnly)
        {
            family = QuotaFamily.Unknown;
            normalized = new NormalizedModel(
                string.IsNullOrWhiteSpace(normalized.RawSlug) ? requested ?? "" : normalized.RawSlug,
                string.IsNullOrWhiteSpace(normalized.DisplayName) || normalized.DisplayName == "Unknown"
                    ? requested ?? "Unknown"
                    : normalized.DisplayName,
                QuotaFamily.Unknown,
                mapped: false);
        }

        var times = ResolveTimes(group, nodes);
        var countable = times.Provenance != TimestampProvenance.Unknown
                        && modelConfidence != ModelEvidenceConfidence.Conflicting;
        var unresolved = UnresolvedEvidenceKind.None;
        if (key.StartsWith("unresolved:", StringComparison.OrdinalIgnoreCase))
        {
            countable = false;
            unresolved = UnresolvedEvidenceKind.Identity;
        }
        else if (times.Provenance == TimestampProvenance.Unknown)
        {
            countable = false;
            unresolved = UnresolvedEvidenceKind.Timestamp;
        }
        else if (modelConfidence == ModelEvidenceConfidence.Conflicting)
        {
            countable = false;
            unresolved = UnresolvedEvidenceKind.Model;
        }

        var requestId = FirstNonEmpty(group.Select(n => n.RequestId));
        var identityHigh = group.Any(n => !string.IsNullOrWhiteSpace(n.RequestId));
        var created = times.CreatedAt ?? DateTimeOffset.MinValue;
        return new UsageEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            RequestId = requestId,
            ConversationId = context.ConversationId,
            MessageId = representative.MessageId ?? representative.Id,
            CreatedAt = created == DateTimeOffset.MinValue ? default : created,
            RequestStartedAt = times.StartedAt,
            ResponseCompletedAt = times.CompletedAt,
            TimestampProvenance = times.Provenance,
            RequestedModel = requested,
            ResponseModel = response,
            NormalizedModel = normalized.DisplayName,
            RawModel = string.IsNullOrWhiteSpace(normalized.RawSlug) ? (response ?? requested ?? "") : normalized.RawSlug,
            ReasoningEffort = effort,
            Source = context.Source,
            ProjectId = context.ProjectId,
            IsArchived = context.Archived,
            FirstSeenAt = now,
            LastSeenAt = now,
            QuotaFamily = family,
            DedupeKey = key,
            DedupeConfidence = identityHigh ? DedupeConfidence.High : DedupeConfidence.Heuristic,
            ModelConfidence = modelConfidence,
            PeriodAmbiguous = false,
            Countable = countable && unresolved == UnresolvedEvidenceKind.None,
            UnresolvedKind = unresolved,
            ReconstructionVersion = ConversationFetchBackoff.ReconstructionSemanticsVersion,
            IdentityAliases = string.Join("|", group
                .Select(n => n.MessageId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase))
        };
    }

    private static (DateTimeOffset? CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, TimestampProvenance Provenance)
        ResolveTimes(List<UsageObservation> group, IReadOnlyDictionary<string, UsageObservation> nodes)
    {
        var fragmentTimes = group.Where(n => n.CreatedAt is not null).Select(n => n.CreatedAt!.Value).OrderBy(t => t).ToList();
        DateTimeOffset? started = fragmentTimes.Count > 0 ? fragmentTimes[0] : null;
        DateTimeOffset? completed = fragmentTimes.Count > 0 ? fragmentTimes[^1] : null;
        if (completed is not null)
        {
            return (completed, started, completed, TimestampProvenance.ResponseFragment);
        }

        if (started is not null)
        {
            return (started, started, null, TimestampProvenance.RequestStart);
        }

        var representative = SelectRepresentative(group);
        if (representative is null)
        {
            return (null, null, null, TimestampProvenance.Unknown);
        }

        var userId = FindUserAncestor(nodes, representative);
        if (userId is not null && nodes.TryGetValue(userId, out var user) && user.CreatedAt is DateTimeOffset userTime)
        {
            var regeneration = IsRegeneration(group, nodes, userId);
            if (!regeneration)
            {
                return (userTime, userTime, null, TimestampProvenance.LinkedUserThisInvocation);
            }
        }

        return (null, null, null, TimestampProvenance.Unknown);
    }

    private static bool IsRegeneration(
        List<UsageObservation> group,
        IReadOnlyDictionary<string, UsageObservation> nodes,
        string userId)
    {
        var finalsInGroup = group.Count(IsVisibleFinal);
        if (finalsInGroup > 1)
        {
            return true;
        }

        var siblings = nodes.Values.Count(n =>
            IsAssistantLike(n)
            && IsVisibleFinal(n)
            && string.Equals(FindUserAncestor(nodes, n), userId, StringComparison.OrdinalIgnoreCase));
        return siblings > 1;
    }

    private static ModelEvidenceConfidence ModelConfidence(string? requested, List<string> responseSlugs, QuotaFamily family)
    {
        if (responseSlugs.Count > 1)
        {
            return ModelEvidenceConfidence.Conflicting;
        }

        if (responseSlugs.Count == 1)
        {
            return family == QuotaFamily.Unknown ? ModelEvidenceConfidence.Unknown : ModelEvidenceConfidence.ObservedResponse;
        }

        if (!string.IsNullOrWhiteSpace(requested))
        {
            return ModelEvidenceConfidence.RequestedOnly;
        }

        return ModelEvidenceConfidence.Unknown;
    }

    private static string? FindLinkedRequestGroup(
        IReadOnlyDictionary<string, List<UsageObservation>> groups,
        IReadOnlyDictionary<string, UsageObservation> nodes,
        UsageObservation node)
    {
        var user = FindUserAncestor(nodes, node);
        string? best = null;
        foreach (var (key, members) in groups)
        {
            if (!key.StartsWith("req:", StringComparison.OrdinalIgnoreCase) || members.Count == 0)
            {
                continue;
            }

            var memberRequest = members.Select(m => m.RequestId).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
            if (!string.IsNullOrWhiteSpace(node.RequestId)
                && !string.IsNullOrWhiteSpace(memberRequest)
                && !string.Equals(node.RequestId, memberRequest, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (user is null || members.All(member => FindUserAncestor(nodes, member) != user))
            {
                continue;
            }

            if (IsVisibleFinal(node) && members.Any(IsVisibleFinal))
            {
                continue;
            }

            if (!members.Any(member => CompatibleBranch(nodes, node, member)))
            {
                continue;
            }

            best = key;
            break;
        }

        return best;
    }

    private static bool CompatibleBranch(
        IReadOnlyDictionary<string, UsageObservation> nodes,
        UsageObservation left,
        UsageObservation right)
    {
        if (string.Equals(left.ParentMessageId, right.MessageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(right.ParentMessageId, left.MessageId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.ParentMessageId, right.ParentMessageId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Ancestors(nodes, left).Contains(right.MessageId ?? right.Id)
               || Ancestors(nodes, right).Contains(left.MessageId ?? left.Id);
    }

    private static HashSet<string> Ancestors(IReadOnlyDictionary<string, UsageObservation> nodes, UsageObservation node)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cursor = node.ParentMessageId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && guard++ < 64)
        {
            ids.Add(cursor);
            if (!nodes.TryGetValue(cursor, out var parent))
            {
                break;
            }

            cursor = parent.ParentMessageId;
        }

        return ids;
    }

    private static string? FindUserAncestor(IReadOnlyDictionary<string, UsageObservation> nodes, UsageObservation node)
    {
        if (!string.IsNullOrWhiteSpace(node.UserAncestorId))
        {
            return node.UserAncestorId;
        }

        var cursor = node.ParentMessageId;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(cursor) && guard++ < 64)
        {
            if (!nodes.TryGetValue(cursor, out var parent))
            {
                return cursor;
            }

            if (string.Equals(parent.Role, "user", StringComparison.OrdinalIgnoreCase))
            {
                return parent.MessageId ?? parent.Id;
            }

            cursor = parent.ParentMessageId;
        }

        return null;
    }

    private static string? FindParentRequestedModel(IReadOnlyDictionary<string, UsageObservation> nodes, UsageObservation node)
    {
        var user = FindUserAncestor(nodes, node);
        if (user is null || !nodes.TryGetValue(user, out var parent))
        {
            return null;
        }

        return FirstNonEmpty(parent.RequestedModel, parent.RawModel, parent.ResponseModel);
    }

    private static bool IsVisibleFinal(UsageObservation node) =>
        !node.Hidden
        && node.EndTurn != false
        && (string.IsNullOrWhiteSpace(node.Recipient)
            || string.Equals(node.Recipient, "all", StringComparison.OrdinalIgnoreCase));

    private static bool IsAssistantLike(UsageObservation node) =>
        string.Equals(node.Role, "assistant", StringComparison.OrdinalIgnoreCase);

    private static UsageObservation? SelectRepresentative(List<UsageObservation> group) =>
        group
            .OrderBy(n => n.Hidden)
            .ThenByDescending(n => n.EndTurn == true)
            .ThenByDescending(n => n.CreatedAt)
            .ThenBy(n => n.MessageId)
            .FirstOrDefault();

    private static void AddGroup(IDictionary<string, List<UsageObservation>> groups, string key, UsageObservation node)
    {
        if (!groups.TryGetValue(key, out var list))
        {
            list = [];
            groups[key] = list;
        }

        list.Add(node);
    }

    private static QuotaFamily RefineFamily(QuotaFamily family, ReasoningEffort effort, string rawSlug)
    {
        if (family is QuotaFamily.GptPro or QuotaFamily.SolReasoning)
        {
            return family;
        }

        if (family == QuotaFamily.Instant && effort is ReasoningEffort.Medium or ReasoningEffort.High or ReasoningEffort.ExtraHigh)
        {
            return QuotaFamily.SolReasoning;
        }

        if (family == QuotaFamily.Unknown
            && effort is ReasoningEffort.Medium or ReasoningEffort.High or ReasoningEffort.ExtraHigh
            && rawSlug.Contains("5-6", StringComparison.OrdinalIgnoreCase))
        {
            return QuotaFamily.SolReasoning;
        }

        return family;
    }

    private static string? FirstNonEmpty(IEnumerable<string?> values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? FirstNonEmpty(params string?[] values) => FirstNonEmpty((IEnumerable<string?>)values);

    private static IEnumerable<string> SplitAliases(string? packed)
    {
        if (string.IsNullOrWhiteSpace(packed))
        {
            yield break;
        }

        foreach (var part in packed.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }

    private static string? ResolveAlias(IReadOnlyDictionary<string, string> aliases, UsageEvent usage)
    {
        foreach (var candidate in CandidateKeys(usage))
        {
            if (aliases.TryGetValue(candidate, out var key))
            {
                return key;
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidateKeys(UsageEvent usage)
    {
        yield return usage.DedupeKey;
        if (!string.IsNullOrWhiteSpace(usage.RequestId))
        {
            yield return UsageEvent.ScopedRequestKey(usage.ConversationId, usage.RequestId);
            yield return "req:" + usage.RequestId;
        }

        if (!string.IsNullOrWhiteSpace(usage.MessageId))
        {
            yield return "msg:" + usage.ConversationId + ":" + usage.MessageId;
        }

        foreach (var alias in SplitAliases(usage.IdentityAliases))
        {
            yield return alias;
            if (!string.IsNullOrWhiteSpace(usage.ConversationId))
            {
                yield return "msg:" + usage.ConversationId + ":" + alias;
            }
        }
    }

    private static UsageEvent Combine(UsageEvent prior, UsageEvent incoming)
    {
        var requestId = FirstNonEmpty(prior.RequestId, incoming.RequestId);
        var conversationId = FirstNonEmpty(prior.ConversationId, incoming.ConversationId) ?? "";
        var preferredKey = !string.IsNullOrWhiteSpace(requestId)
            ? UsageEvent.ScopedRequestKey(conversationId, requestId)
            : FirstNonEmpty(incoming.DedupeKey, prior.DedupeKey) ?? prior.DedupeKey;
        var aliases = string.Join("|", SplitAliases(prior.IdentityAliases)
            .Concat(SplitAliases(incoming.IdentityAliases))
            .Append(prior.DedupeKey)
            .Append(incoming.DedupeKey)
            .Append(prior.MessageId)
            .Append(incoming.MessageId)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        var provenance = StrongerProvenance(prior.TimestampProvenance, incoming.TimestampProvenance);
        var created = ChooseTimestamp(prior, incoming, provenance);
        var family = CombineFamily(prior, incoming);
        return new UsageEvent
        {
            Id = prior.Id,
            RequestId = requestId,
            ConversationId = conversationId,
            MessageId = FirstNonEmpty(incoming.MessageId, prior.MessageId),
            CreatedAt = created,
            RequestStartedAt = MinTime(prior.RequestStartedAt, incoming.RequestStartedAt),
            ResponseCompletedAt = MaxTime(prior.ResponseCompletedAt, incoming.ResponseCompletedAt),
            TimestampProvenance = provenance,
            RequestedModel = FirstNonEmpty(prior.RequestedModel, incoming.RequestedModel),
            ResponseModel = FirstNonEmpty(incoming.ResponseModel, prior.ResponseModel),
            NormalizedModel = family.display,
            RawModel = FirstNonEmpty(incoming.RawModel, prior.RawModel) ?? "",
            ReasoningEffort = prior.ReasoningEffort == ReasoningEffort.Unknown ? incoming.ReasoningEffort : prior.ReasoningEffort,
            Source = PreferSource(prior.Source, incoming.Source),
            ProjectId = FirstNonEmpty(incoming.ProjectId, prior.ProjectId),
            IsArchived = incoming.IsArchived || prior.IsArchived,
            FirstSeenAt = prior.FirstSeenAt <= incoming.FirstSeenAt ? prior.FirstSeenAt : incoming.FirstSeenAt,
            LastSeenAt = incoming.LastSeenAt >= prior.LastSeenAt ? incoming.LastSeenAt : prior.LastSeenAt,
            QuotaFamily = family.family,
            DedupeKey = preferredKey,
            DedupeConfidence = prior.DedupeConfidence == DedupeConfidence.High || incoming.DedupeConfidence == DedupeConfidence.High
                ? DedupeConfidence.High
                : DedupeConfidence.Heuristic,
            ModelConfidence = incoming.ModelConfidence == ModelEvidenceConfidence.Conflicting
                || prior.ModelConfidence == ModelEvidenceConfidence.Conflicting
                ? ModelEvidenceConfidence.Conflicting
                : incoming.ModelConfidence != ModelEvidenceConfidence.Unspecified
                    ? incoming.ModelConfidence
                    : prior.ModelConfidence,
            PeriodAmbiguous = prior.PeriodAmbiguous || incoming.PeriodAmbiguous,
            Countable = prior.Countable && incoming.Countable,
            UnresolvedKind = incoming.UnresolvedKind != UnresolvedEvidenceKind.None ? incoming.UnresolvedKind : prior.UnresolvedKind,
            ReconstructionVersion = Math.Max(prior.ReconstructionVersion, incoming.ReconstructionVersion),
            CorrectionReason = incoming.CorrectionReason ?? prior.CorrectionReason
                ?? (string.Equals(prior.DedupeKey, preferredKey, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : "identity_merge"),
            IdentityAliases = aliases
        };
    }

    private static (QuotaFamily family, string display) CombineFamily(UsageEvent prior, UsageEvent incoming)
    {
        if (incoming.ModelConfidence == ModelEvidenceConfidence.Conflicting
            || prior.ModelConfidence == ModelEvidenceConfidence.Conflicting)
        {
            return (QuotaFamily.Unknown, "Unknown");
        }

        if (incoming.QuotaFamily != QuotaFamily.Unknown)
        {
            return (incoming.QuotaFamily, FirstNonEmpty(incoming.NormalizedModel, prior.NormalizedModel) ?? "");
        }

        if (prior.QuotaFamily != QuotaFamily.Unknown && incoming.ModelConfidence != ModelEvidenceConfidence.Unknown)
        {
            return (prior.QuotaFamily, FirstNonEmpty(prior.NormalizedModel, incoming.NormalizedModel) ?? "");
        }

        return (prior.QuotaFamily, FirstNonEmpty(prior.NormalizedModel, incoming.NormalizedModel) ?? "");
    }

    private static TimestampProvenance StrongerProvenance(TimestampProvenance left, TimestampProvenance right)
    {
        return Rank(left) >= Rank(right) ? left : right;

        static int Rank(TimestampProvenance value) => value switch
        {
            TimestampProvenance.ResponseFragment => 4,
            TimestampProvenance.RequestStart => 3,
            TimestampProvenance.LinkedUserThisInvocation => 2,
            TimestampProvenance.Unspecified => 1,
            TimestampProvenance.LegacyUnverified => 1,
            _ => 0
        };
    }

    private static DateTimeOffset ChooseTimestamp(UsageEvent prior, UsageEvent incoming, TimestampProvenance provenance)
    {
        var preferred = provenance == incoming.TimestampProvenance ? incoming : prior;
        if (preferred.HasUsableTimestamp)
        {
            return preferred.CreatedAt;
        }

        if (prior.HasUsableTimestamp)
        {
            return prior.CreatedAt;
        }

        return incoming.HasUsableTimestamp ? incoming.CreatedAt : prior.CreatedAt;
    }

    private static DateTimeOffset? MinTime(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left < right ? left : right;

    private static DateTimeOffset? MaxTime(DateTimeOffset? left, DateTimeOffset? right) =>
        left is null ? right : right is null ? left : left > right ? left : right;

    private static UsageSource PreferSource(UsageSource left, UsageSource right) =>
        left == UsageSource.OfficialExport || right == UsageSource.OfficialExport
            ? UsageSource.OfficialExport
            : right;

    private static UsageEvent Clone(UsageEvent usage) => new()
    {
        Id = usage.Id,
        RequestId = usage.RequestId,
        ConversationId = usage.ConversationId,
        MessageId = usage.MessageId,
        CreatedAt = usage.CreatedAt,
        RequestStartedAt = usage.RequestStartedAt,
        ResponseCompletedAt = usage.ResponseCompletedAt,
        TimestampProvenance = usage.TimestampProvenance,
        RequestedModel = usage.RequestedModel,
        ResponseModel = usage.ResponseModel,
        NormalizedModel = usage.NormalizedModel,
        RawModel = usage.RawModel,
        ReasoningEffort = usage.ReasoningEffort,
        Source = usage.Source,
        ProjectId = usage.ProjectId,
        IsArchived = usage.IsArchived,
        FirstSeenAt = usage.FirstSeenAt,
        LastSeenAt = usage.LastSeenAt,
        QuotaFamily = usage.QuotaFamily,
        DedupeKey = usage.DedupeKey,
        DedupeConfidence = usage.DedupeConfidence,
        ModelConfidence = usage.ModelConfidence,
        PeriodAmbiguous = usage.PeriodAmbiguous,
        Countable = usage.Countable,
        UnresolvedKind = usage.UnresolvedKind,
        ReconstructionVersion = usage.ReconstructionVersion,
        CorrectionReason = usage.CorrectionReason,
        IdentityAliases = usage.IdentityAliases
    };
}
