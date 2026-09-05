using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Tests;

public class ProServerStatusRefreshTests
{
    [Fact]
    public async Task Refresh_DoesNotScanConversations()
    {
        var provider = new RecordingQuotaProvider();
        var service = new ProServerStatusService(new ProServerStatusStore(TempFile()), new MutableClock(DateTimeOffset.UtcNow));
        await service.RefreshAsync(provider);
        Assert.Equal(1, provider.QuotaCalls);
        Assert.Equal(0, provider.IndexCalls);
        Assert.Equal(0, provider.ConversationCalls);
        Assert.False(provider.SentModelTurn);
    }

    [Fact]
    public async Task Refresh_IsSingleFlight()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var provider = new RecordingQuotaProvider
        {
            Gate = async () =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        var service = new ProServerStatusService(new ProServerStatusStore(TempFile()), new MutableClock(DateTimeOffset.UtcNow));
        var first = service.RefreshAsync(provider);
        await started.Task;
        var second = service.RefreshAsync(provider);
        Assert.Equal(1, provider.QuotaCalls);
        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, provider.QuotaCalls);
    }

    [Fact]
    public async Task Cancellation_StopsWaiterWithoutLosingLastStatus()
    {
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var provider = new RecordingQuotaProvider
        {
            Gate = async () =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        var service = new ProServerStatusService(new ProServerStatusStore(TempFile()), new MutableClock(DateTimeOffset.UtcNow));
        using var cts = new CancellationTokenSource();
        var owner = service.RefreshAsync(provider);
        await started.Task;
        var waiter = service.RefreshAsync(provider, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult();
        await owner;
        Assert.True(service.Current.ServerObserved);
    }

    [Fact]
    public async Task TransientFailure_RetainsLastSuccessfulAsStale()
    {
        var provider = new RecordingQuotaProvider();
        var service = new ProServerStatusService(new ProServerStatusStore(TempFile()), new MutableClock(DateTimeOffset.UtcNow));
        await service.RefreshAsync(provider);
        Assert.False(service.Current.Stale);
        provider.FailNext = true;
        var result = await service.RefreshAsync(provider);
        Assert.True(result.TransientFailure);
        Assert.True(result.UsedCache);
        Assert.True(service.Current.Stale);
        Assert.Equal(ProRestrictionState.CorrelatedRestriction, service.Current.RestrictionState);
    }

    [Fact]
    public void ResetRecheck_IsBounded()
    {
        var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var reset = now.AddMinutes(30);
        var due = ProServerStatusService.NextResetRecheck(new ProServerStatus { ResetAt = reset }, now);
        Assert.NotNull(due);
        Assert.InRange(due.Value, reset + ProServerStatusService.ResetRecheckMin, reset + ProServerStatusService.ResetRecheckMax);
        Assert.Null(ProServerStatusService.NextResetRecheck(new ProServerStatus { ResetAt = now.AddMinutes(-1) }, now));
    }

    private static string TempFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "prometer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "pro-status.json");
    }

    private sealed class RecordingQuotaProvider : IChatGptProvider
    {
        public int QuotaCalls { get; private set; }
        public int IndexCalls { get; private set; }
        public int ConversationCalls { get; private set; }
        public bool SentModelTurn { get; }
        public bool FailNext { get; set; }
        public Func<Task>? Gate { get; set; }

        public Task<AccountStatus> GetAccountStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountStatus { IsSignedIn = true });

        public Task<IReadOnlyList<ModelCatalogEntry>> GetModelCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelCatalogEntry>>([]);

        public Task<ConversationIndexResult> GetConversationIndexAsync(bool archived, double? minUpdateTime = null, CancellationToken cancellationToken = default)
        {
            IndexCalls++;
            return Task.FromResult(new ConversationIndexResult());
        }

        public Task<ConversationIndexResult> GetArchivedConversationIndexAsync(double? minUpdateTime = null, CancellationToken cancellationToken = default)
        {
            IndexCalls++;
            return Task.FromResult(new ConversationIndexResult());
        }

        public Task<ProjectListResult> GetProjectsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProjectListResult());

        public Task<ConversationIndexResult> GetProjectConversationsAsync(string projectId, double? minUpdateTime = null, CancellationToken cancellationToken = default)
        {
            IndexCalls++;
            return Task.FromResult(new ConversationIndexResult());
        }

        public Task<ConversationLoadResult> GetConversationMessagesAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ConversationCalls++;
            return Task.FromResult(new ConversationLoadResult());
        }

        public async Task<QuotaMetadataSet> TryGetQuotaMetadataAsync(CancellationToken cancellationToken = default)
        {
            QuotaCalls++;
            if (Gate is not null)
            {
                await Gate();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (FailNext)
            {
                FailNext = false;
                throw new ChatGptProviderException("offline", 0);
            }

            return AccountParser.ParseQuotaMetadata(new JsonObject
            {
                ["model_limits"] = new JsonArray
                {
                    new JsonObject { ["model_slug"] = "gpt-6-pro", ["resets_after"] = "2026-09-06T05:20:13Z" }
                },
                ["blocked_features"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "reason",
                        ["limit"] = 50,
                        ["resets_after"] = "2026-09-06T05:20:13Z"
                    }
                }
            });
        }
    }
}
