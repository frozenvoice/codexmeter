namespace ProMeter.Providers.ChatGpt;

public static class ConversationIndexPager
{
    public const int RequestedLimit = 100;
    public const int MaxPages = 80;

    public static IndexPageInfo ReadPage(JsonNode? root, int requestedOffset, int requestedLimit, int returnedCount, bool reachedCutoff)
    {
        var total = (int?)ChatGptJson.GetDouble(root, "total", "total_count");
        var offset = (int?)ChatGptJson.GetDouble(root, "offset") ?? requestedOffset;
        var limit = (int?)ChatGptJson.GetDouble(root, "limit") ?? Math.Max(returnedCount, 1);
        var nextOffset = (int?)ChatGptJson.GetDouble(root, "next_offset", "nextOffset");
        var hasMore = ChatGptJson.GetBool(root, "has_more", "hasMore");
        return new IndexPageInfo
        {
            Total = total,
            Offset = offset,
            Limit = limit,
            NextOffset = nextOffset,
            HasMore = hasMore,
            ReturnedCount = returnedCount,
            RequestedLimit = requestedLimit,
            ReachedCutoff = reachedCutoff
        };
    }

    public static bool ShouldFetchNext(IndexPageInfo page, int currentOffset, ISet<int> seenNextOffsets)
    {
        if (page.ReachedCutoff || page.ReturnedCount == 0)
        {
            return false;
        }

        if (page.HasMore == false)
        {
            return false;
        }

        var next = page.NextOffset ?? (currentOffset + page.ReturnedCount);
        if (next <= currentOffset || !seenNextOffsets.Add(next))
        {
            return false;
        }

        if (page.Total is int total && next >= total)
        {
            return false;
        }

        if (page.HasMore == true)
        {
            return true;
        }

        if (page.Total is int remaining && next < remaining)
        {
            return true;
        }

        // Do not treat returnedCount < requestedLimit as completion.
        // An unknown has_more with a non-empty page may still have more
        // items when the server capped the page below the requested limit.
        return page.HasMore is null && page.ReturnedCount > 0 && page.Total is null;
    }

    public static CursorPageInfo ReadCursor(JsonNode? root)
    {
        return new CursorPageInfo
        {
            HasMore = ChatGptJson.GetBool(root, "has_more", "hasMore"),
            NextCursor = ChatGptJson.GetString(root, "next_cursor", "cursor", "nextCursor"),
            ReturnedCount = (root?["items"] as JsonArray)?.Count
                ?? (root?["conversations"] as JsonArray)?.Count
                ?? 0
        };
    }

    public static bool ShouldFetchNextCursor(string? currentCursor, CursorPageInfo page, ISet<string> seenCursors)
    {
        if (page.ReturnedCount == 0 || page.HasMore == false)
        {
            return false;
        }

        var next = page.NextCursor;
        if (string.IsNullOrWhiteSpace(next) || next == currentCursor || !seenCursors.Add(next))
        {
            return false;
        }

        return page.HasMore == true || page.HasMore is null;
    }
}

public sealed class IndexPageInfo
{
    public int? Total { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; }
    public int? NextOffset { get; init; }
    public bool? HasMore { get; init; }
    public int ReturnedCount { get; init; }
    public int RequestedLimit { get; init; }
    public bool ReachedCutoff { get; init; }
}

public sealed class CursorPageInfo
{
    public bool? HasMore { get; init; }
    public string? NextCursor { get; init; }
    public int ReturnedCount { get; init; }
}

public sealed class ConversationIndexResult
{
    public IReadOnlyList<ConversationIndexItem> Items { get; init; } = [];
    public bool ReachedCutoff { get; init; }
    public bool Incomplete { get; init; }
    public int Pages { get; init; }
}

public sealed class ProjectListResult
{
    public IReadOnlyList<ProjectInfo> Projects { get; init; } = [];
    public bool Incomplete { get; init; }
    public int Pages { get; init; }
}
