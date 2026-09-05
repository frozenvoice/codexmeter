using ProMeter.Models;
using ProMeter.Services;

namespace ProMeter.Codex;

public sealed record CombinedRefreshResult(
    SyncOutcome? ChatGpt,
    CodexRefreshResult Codex,
    bool PartialFailure,
    bool TotalFailure);

public sealed class CombinedRefreshCoordinator
{
    private readonly Func<bool, CancellationToken, Task<SyncOutcome>> _chatGpt;
    private readonly Func<CancellationToken, Task<CodexRefreshResult>> _codex;

    public CombinedRefreshCoordinator(
        Func<bool, CancellationToken, Task<SyncOutcome>> chatGpt,
        Func<CancellationToken, Task<CodexRefreshResult>> codex)
    {
        _chatGpt = chatGpt;
        _codex = codex;
    }

    public bool ChatGptRefreshing { get; private set; }
    public bool CodexRefreshing { get; private set; }
    public bool ManualRefreshInProgress { get; private set; }
    public bool BothRefreshing => ChatGptRefreshing && CodexRefreshing;
    public bool RefreshButtonEnabled => !ManualRefreshInProgress;
    public bool RefreshButtonActive => ManualRefreshInProgress || ChatGptRefreshing || CodexRefreshing;

    public event Action? StateChanged;

    public async Task<CombinedRefreshResult> RefreshAllAsync(bool forceChatGpt, CancellationToken cancellationToken)
    {
        ManualRefreshInProgress = true;
        var chatStarted = !ChatGptRefreshing;
        var codexStarted = !CodexRefreshing;
        if (chatStarted)
        {
            ChatGptRefreshing = true;
        }

        if (codexStarted)
        {
            CodexRefreshing = true;
        }

        Raise();
        try
        {
            var chatTask = chatStarted
                ? SafeChatGpt(forceChatGpt, cancellationToken)
                : Task.FromResult<SyncOutcome?>(null);
            var codexTask = codexStarted
                ? SafeCodex(cancellationToken)
                : Task.FromResult(new CodexRefreshResult(
                    CodexQuotaSnapshot.Empty(CodexQuotaStatus.Refreshing),
                    UsedCache: true,
                    "already-running"));

            await Task.WhenAll(chatTask, codexTask).ConfigureAwait(false);
            var chat = await chatTask.ConfigureAwait(false);
            var codex = await codexTask.ConfigureAwait(false);
            var chatFailed = chat is not null && IsChatGptFailure(chat);
            var codexFailed = IsCodexFailure(codex);
            return new CombinedRefreshResult(
                chat,
                codex,
                PartialFailure: chatFailed ^ codexFailed || (chat is null && codexFailed),
                TotalFailure: chatFailed && codexFailed);
        }
        finally
        {
            if (chatStarted)
            {
                ChatGptRefreshing = false;
            }

            if (codexStarted)
            {
                CodexRefreshing = false;
            }

            ManualRefreshInProgress = false;
            Raise();
        }
    }

    public static FlyoutRefreshPresentation Present(
        bool chatGptRefreshing,
        bool codexRefreshing,
        bool combinedManual = false) =>
        new(
            !combinedManual,
            combinedManual || chatGptRefreshing || codexRefreshing,
            combinedManual || chatGptRefreshing || codexRefreshing ? UiText.RefreshAllProgress : "");

    private async Task<CodexRefreshResult> SafeCodex(CancellationToken cancellationToken)
    {
        try
        {
            return await _codex(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new CodexRefreshResult(
                CodexQuotaSnapshot.Empty(
                    cancellationToken.IsCancellationRequested ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut,
                    cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out"),
                UsedCache: true,
                cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out");
        }
    }

    private async Task<SyncOutcome?> SafeChatGpt(bool force, CancellationToken cancellationToken)
    {
        try
        {
            return await _chatGpt(force, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SyncOutcome(AppSyncStatus.Idle, "cancelled", 0);
        }
        catch (Exception ex)
        {
            return new SyncOutcome(AppSyncStatus.Error, CodexProtocol.SanitizeDiagnostic(ex.GetType().Name, 80), 0);
        }
    }

    private static bool IsChatGptFailure(SyncOutcome outcome) =>
        outcome.Status is AppSyncStatus.Error
            or AppSyncStatus.Offline
            or AppSyncStatus.AuthenticationRequired
            or AppSyncStatus.SignedOut
            or AppSyncStatus.Forbidden
            or AppSyncStatus.ProviderSchemaMismatch
            or AppSyncStatus.CompanionDisconnected
            or AppSyncStatus.BridgeTimeout
            or AppSyncStatus.BridgeWriteFailed;

    private static bool IsCodexFailure(CodexRefreshResult result) =>
        result.Snapshot.Status is CodexQuotaStatus.Unavailable
            or CodexQuotaStatus.ProtocolMismatch
            or CodexQuotaStatus.TimedOut
            or CodexQuotaStatus.Cancelled
            or CodexQuotaStatus.CodexNotFound
            or CodexQuotaStatus.SignedOut;

    private void Raise() => StateChanged?.Invoke();
}

public readonly record struct FlyoutRefreshPresentation(bool Enabled, bool Active, string ProgressText);
