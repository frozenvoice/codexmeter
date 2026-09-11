using System.Text.Json.Nodes;
using CycleArc.Providers.ChatGpt;

namespace CycleArc.Tests;

public class PaginationTests
{
    [Fact]
    public void ShouldFetchNext_DoesNotStopWhenPageIsSmallerThanRequestedLimit()
    {
        var seen = new HashSet<int>();
        var page = ConversationIndexPager.ReadPage(
            new JsonObject
            {
                ["total"] = 84,
                ["offset"] = 0,
                ["limit"] = 28,
                ["has_more"] = true,
                ["next_offset"] = 28
            },
            requestedOffset: 0,
            requestedLimit: 100,
            returnedCount: 28,
            reachedCutoff: false);

        Assert.Equal(84, page.Total);
        Assert.Equal(28, page.Limit);
        Assert.True(ConversationIndexPager.ShouldFetchNext(page, 0, seen));
        Assert.Contains(28, seen);
    }

    [Fact]
    public void ShouldFetchNext_StopsOnRepeatedOffset()
    {
        var seen = new HashSet<int> { 28 };
        var page = ConversationIndexPager.ReadPage(
            new JsonObject { ["has_more"] = true, ["next_offset"] = 28 },
            0,
            100,
            28,
            false);
        Assert.False(ConversationIndexPager.ShouldFetchNext(page, 0, seen));
    }

    [Fact]
    public void ShouldFetchNext_HonorsTotalWithoutHasMore()
    {
        var seen = new HashSet<int>();
        var first = ConversationIndexPager.ReadPage(
            new JsonObject { ["total"] = 56, ["offset"] = 0, ["limit"] = 28 },
            0,
            100,
            28,
            false);
        Assert.True(ConversationIndexPager.ShouldFetchNext(first, 0, seen));

        var last = ConversationIndexPager.ReadPage(
            new JsonObject { ["total"] = 56, ["offset"] = 28, ["limit"] = 28 },
            28,
            100,
            28,
            false);
        Assert.False(ConversationIndexPager.ShouldFetchNext(last, 28, seen));
    }

    [Fact]
    public async Task FetchIndex_ConsumesCappedPagesUntilCutoff()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const int total = 84;
        const int pageSize = 28;
        var items = Enumerable.Range(0, total)
            .Select(i => new ConversationIndexItem
            {
                Id = "c-" + i,
                UpdateTime = now - i,
                CreateTime = now - i - 10
            })
            .ToList();
        var cutoff = items[49].UpdateTime;
        var transport = new ScriptedTransport((method, path) =>
        {
            Assert.Equal("GET", method);
            if (path.Contains("is_archived=true", StringComparison.Ordinal))
            {
                return Page(Array.Empty<ConversationIndexItem>(), 0, pageSize, 0);
            }

            var offset = ReadQueryInt(path, "offset") ?? 0;
            var requested = ReadQueryInt(path, "limit") ?? 100;
            Assert.Equal(100, requested);
            return Page(items, offset, pageSize, total);
        });

        var provider = new ChatGptProvider(transport);
        var active = await provider.GetConversationIndexAsync(false, cutoff);
        Assert.False(active.Incomplete);
        Assert.True(active.ReachedCutoff);
        Assert.Equal(50, active.Items.Count);
        Assert.Equal(2, active.Pages);
        Assert.Equal("c-0", active.Items[0].Id);
        Assert.Equal("c-49", active.Items[^1].Id);

        var archived = await provider.GetArchivedConversationIndexAsync(cutoff);
        Assert.Empty(archived.Items);
        Assert.Equal(1, archived.Pages);
    }

    [Fact]
    public async Task ProjectsSidebar_FollowsCursorPages()
    {
        var transport = new ScriptedTransport((_, path) =>
        {
            if (path.Contains("cursor=page-2", StringComparison.Ordinal))
            {
                return Ok(new JsonObject
                {
                    ["items"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "proj-2", ["gizmo"] = new JsonObject { ["id"] = "proj-2", ["display"] = new JsonObject { ["name"] = "Two" } } }
                    },
                    ["has_more"] = false,
                    ["cursor"] = "page-2"
                });
            }

            return Ok(new JsonObject
            {
                ["items"] = new JsonArray
                {
                    new JsonObject { ["id"] = "proj-1", ["gizmo"] = new JsonObject { ["id"] = "proj-1", ["display"] = new JsonObject { ["name"] = "One" } } }
                },
                ["has_more"] = true,
                ["next_cursor"] = "page-2"
            });
        });

        var result = await new ChatGptProvider(transport).GetProjectsAsync();
        Assert.False(result.Incomplete);
        Assert.Equal(2, result.Pages);
        Assert.Equal(new[] { "proj-1", "proj-2" }, result.Projects.Select(p => p.Id));
    }

    [Fact]
    public async Task ProjectsSidebar_FollowsGizmosCursorPages()
    {
        var transport = new ScriptedTransport((_, path) =>
        {
            if (path.Contains("cursor=page-2", StringComparison.Ordinal))
            {
                return Ok(new JsonObject
                {
                    ["gizmos"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "proj-2", ["gizmo"] = new JsonObject { ["id"] = "proj-2", ["display"] = new JsonObject { ["name"] = "Two" } } }
                    },
                    ["has_more"] = false
                });
            }

            return Ok(new JsonObject
            {
                ["gizmos"] = new JsonArray
                {
                    new JsonObject { ["id"] = "proj-1", ["gizmo"] = new JsonObject { ["id"] = "proj-1", ["display"] = new JsonObject { ["name"] = "One" } } }
                },
                ["has_more"] = true,
                ["next_cursor"] = "page-2"
            });
        });

        var result = await new ChatGptProvider(transport).GetProjectsAsync();
        Assert.False(result.Incomplete);
        Assert.False(result.SchemaMismatch);
        Assert.Equal(2, result.Pages);
        Assert.Equal(new[] { "proj-1", "proj-2" }, result.Projects.Select(p => p.Id));
    }

    [Fact]
    public async Task ProjectConversationIndex_FollowsConversationsCursorPages()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var transport = new ScriptedTransport((_, path) =>
        {
            Assert.Contains("/backend-api/gizmos/proj-1/conversations", path, StringComparison.Ordinal);
            if (path.Contains("cursor=page-2", StringComparison.Ordinal))
            {
                return Ok(new JsonObject
                {
                    ["conversations"] = new JsonArray
                    {
                        new JsonObject { ["id"] = "c-2", ["update_time"] = now - 1, ["create_time"] = now - 11 }
                    },
                    ["has_more"] = false
                });
            }

            return Ok(new JsonObject
            {
                ["conversations"] = new JsonArray
                {
                    new JsonObject { ["id"] = "c-1", ["update_time"] = now, ["create_time"] = now - 10 }
                },
                ["has_more"] = true,
                ["next_cursor"] = "page-2"
            });
        });

        var result = await new ChatGptProvider(transport).GetProjectConversationsAsync("proj-1");
        Assert.False(result.Incomplete);
        Assert.False(result.SchemaMismatch);
        Assert.Equal(2, result.Pages);
        Assert.Equal(new[] { "c-1", "c-2" }, result.Items.Select(item => item.Id));
    }

    [Fact]
    public async Task HasMoreWithUnrecognizedCollection_IsNotSuccessfulCompletion()
    {
        var transport = new ScriptedTransport((_, _) => Ok(new JsonObject
        {
            ["widgets"] = new JsonArray
            {
                new JsonObject { ["id"] = "hidden-1" }
            },
            ["has_more"] = true,
            ["next_cursor"] = "page-2"
        }));

        var projects = await new ChatGptProvider(transport).GetProjectsAsync();
        Assert.True(projects.SchemaMismatch);
        Assert.True(projects.Incomplete);
        Assert.Empty(projects.Projects);

        var conversations = await new ChatGptProvider(transport).GetConversationIndexAsync(false);
        Assert.True(conversations.SchemaMismatch);
        Assert.True(conversations.Incomplete);
        Assert.Empty(conversations.Items);
    }

    private static ProviderResponse Page(IReadOnlyList<ConversationIndexItem> items, int offset, int pageSize, int total)
    {
        var slice = items.Skip(offset).Take(pageSize).ToList();
        var array = new JsonArray();
        foreach (var item in slice)
        {
            array.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["update_time"] = item.UpdateTime,
                ["create_time"] = item.CreateTime
            });
        }

        var next = offset + slice.Count;
        return Ok(new JsonObject
        {
            ["items"] = array,
            ["total"] = total,
            ["offset"] = offset,
            ["limit"] = pageSize,
            ["has_more"] = next < total,
            ["next_offset"] = next
        });
    }

    private static ProviderResponse Ok(JsonNode body) => new()
    {
        Status = 200,
        Body = body.ToJsonString()
    };

    private static int? ReadQueryInt(string path, string name)
    {
        var query = path.Split('?', 2).ElementAtOrDefault(1);
        if (query is null)
        {
            return null;
        }

        foreach (var part in query.Split('&'))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2 && pair[0] == name && int.TryParse(pair[1], out var value))
            {
                return value;
            }
        }

        return null;
    }

    internal sealed class ScriptedTransport : IChatGptTransport
    {
        private readonly Func<string, string, ProviderResponse> _handler;

        public ScriptedTransport(Func<string, string, ProviderResponse> handler) => _handler = handler;

        public Task<ProviderResponse> SendAsync(string method, string path, string? jsonBody = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_handler(method, path));
    }
}
