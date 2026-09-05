using System.Text;
using System.Text.Json.Nodes;

namespace ProMeter.Codex;

public sealed record CodexProtocolSession(
    CodexQuotaStatus Status,
    JsonNode? AccountResult,
    JsonNode? RateLimitsResult,
    IReadOnlyList<string> SentMethods,
    string SanitizedStderr,
    string? Detail,
    bool ProcessCleanedUp,
    bool KillCalled);

public sealed class CodexAppServerClient
{
    private readonly ICodexProcessFactory _processes;

    public CodexAppServerClient(ICodexProcessFactory? processes = null)
    {
        _processes = processes ?? new CodexProcessFactory();
    }

    public async Task<CodexProtocolSession> ReadQuotaAsync(
        CodexLaunchCommand command,
        string clientVersion,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(CodexProtocol.TotalHardCeilingMs);
        var sent = new List<string>();
        var stderr = new StringBuilder();
        ICodexProcess? process = null;
        try
        {
            process = await StartAsync(command, linked.Token).ConfigureAwait(false);
            _ = process.DrainStderrAsync(stderr, CodexProtocol.MaxStderrBytes, linked.Token);
            await SendAsync(process, CodexProtocol.BuildInitialize(clientVersion), "initialize", sent, linked.Token)
                .ConfigureAwait(false);
            var initialize = await WaitForResponseAsync(
                process,
                CodexProtocol.InitializeId.ToString(),
                CodexProtocol.InitializeTimeoutMs,
                linked.Token).ConfigureAwait(false);
            if (initialize.Status != CodexQuotaStatus.Available)
            {
                return await CompleteAsync(initialize.Status, null, null, sent, stderr, initialize.Detail, process)
                    .ConfigureAwait(false);
            }

            await SendAsync(process, CodexProtocol.BuildInitialized(), "initialized", sent, linked.Token)
                .ConfigureAwait(false);
            await SendAsync(process, CodexProtocol.BuildAccountRead(), "account/read", sent, linked.Token)
                .ConfigureAwait(false);
            var account = await WaitForResponseAsync(
                process,
                CodexProtocol.AccountReadId.ToString(),
                CodexProtocol.AccountReadTimeoutMs,
                linked.Token).ConfigureAwait(false);
            if (account.Status is CodexQuotaStatus.TimedOut or CodexQuotaStatus.Cancelled or CodexQuotaStatus.ProtocolMismatch)
            {
                return await CompleteAsync(account.Status, account.Node, null, sent, stderr, account.Detail, process)
                    .ConfigureAwait(false);
            }

            await SendAsync(process, CodexProtocol.BuildRateLimitsRead(), "account/rateLimits/read", sent, linked.Token)
                .ConfigureAwait(false);
            var limits = await WaitForResponseAsync(
                process,
                CodexProtocol.RateLimitsReadId.ToString(),
                CodexProtocol.RateLimitsReadTimeoutMs,
                linked.Token).ConfigureAwait(false);
            if (limits.Status is CodexQuotaStatus.TimedOut or CodexQuotaStatus.Cancelled or CodexQuotaStatus.ProtocolMismatch)
            {
                return await CompleteAsync(limits.Status, account.Node, limits.Node, sent, stderr, limits.Detail, process)
                    .ConfigureAwait(false);
            }

            return await CompleteAsync(CodexQuotaStatus.Available, account.Node, limits.Node, sent, stderr, null, process)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await CompleteAsync(
                    cancellationToken.IsCancellationRequested ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut,
                    null,
                    null,
                    sent,
                    stderr,
                    cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out",
                    process)
                .ConfigureAwait(false);
        }
        catch (CodexProtocolException ex)
        {
            return await CompleteAsync(CodexQuotaStatus.ProtocolMismatch, null, null, sent, stderr, ex.Message, process)
                .ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return await CompleteAsync(CodexQuotaStatus.CodexNotFound, null, null, sent, stderr, "codex-not-found", process)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await CompleteAsync(CodexQuotaStatus.Unavailable, null, null, sent, stderr, SafeException(ex), process)
                .ConfigureAwait(false);
        }
    }

    private async Task<ICodexProcess> StartAsync(CodexLaunchCommand command, CancellationToken cancellationToken)
    {
        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startCts.CancelAfter(CodexProtocol.ProcessStartTimeoutMs);
        return await Task.Run(() => _processes.Start(command), startCts.Token).ConfigureAwait(false);
    }

    private static async Task SendAsync(
        ICodexProcess process,
        string json,
        string method,
        List<string> sent,
        CancellationToken cancellationToken)
    {
        if (CodexProtocol.IsForbiddenMethod(method))
        {
            throw new CodexProtocolException("Refusing to send a Codex model turn.");
        }

        sent.Add(method);
        await process.WriteLineAsync(json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<WaitedResponse> WaitForResponseAsync(
        ICodexProcess process,
        string expectedId,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stage.CancelAfter(timeoutMs);
        try
        {
            while (!stage.Token.IsCancellationRequested)
            {
                var line = await process.ReadLineAsync(CodexProtocol.MaxJsonLineBytes, stage.Token)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    return new WaitedResponse(CodexQuotaStatus.Unavailable, null, "stdout-closed");
                }

                JsonNode? node;
                try
                {
                    node = CodexProtocol.ParseLine(line);
                }
                catch (CodexProtocolException)
                {
                    return new WaitedResponse(CodexQuotaStatus.ProtocolMismatch, null, "oversized-or-invalid-jsonl");
                }

                if (CodexProtocol.IsNotification(node))
                {
                    continue;
                }

                if (!CodexProtocol.TryGetResponseId(node, out var id) || id != expectedId)
                {
                    continue;
                }

                return new WaitedResponse(CodexQuotaStatus.Available, node, CodexProtocol.HasError(node) ? "response-error" : null);
            }
        }
        catch (OperationCanceledException)
        {
            return new WaitedResponse(
                cancellationToken.IsCancellationRequested ? CodexQuotaStatus.Cancelled : CodexQuotaStatus.TimedOut,
                null,
                cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out");
        }

        return new WaitedResponse(CodexQuotaStatus.TimedOut, null, "timed-out");
    }

    private static async Task<CodexProtocolSession> CompleteAsync(
        CodexQuotaStatus status,
        JsonNode? account,
        JsonNode? limits,
        List<string> methods,
        StringBuilder errors,
        string? detail,
        ICodexProcess? process)
    {
        var killCalled = false;
        var cleaned = true;
        if (process is not null)
        {
            try
            {
                await process.DisposeAsync().ConfigureAwait(false);
                killCalled = process.KillCalled || process.HasExited;
            }
            catch
            {
                try
                {
                    process.KillTree();
                    killCalled = true;
                }
                catch
                {
                    cleaned = false;
                }
            }
        }

        return new CodexProtocolSession(
            status,
            account,
            limits,
            methods,
            CodexProtocol.SanitizeDiagnostic(errors.ToString()),
            detail,
            cleaned,
            killCalled);
    }

    private static string SafeException(Exception ex) =>
        CodexProtocol.SanitizeDiagnostic($"{ex.GetType().Name}:{ex.Message}", 240);

    private readonly record struct WaitedResponse(CodexQuotaStatus Status, JsonNode? Node, string? Detail);
}
