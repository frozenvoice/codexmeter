using System.Text;
using System.Text.Json.Nodes;

namespace CycleArc.Codex;

public enum CreditRedemptionOutcome { Reset, AlreadyRedeemed, NothingToReset, NoCredit, Unknown, Unavailable, Busy }

public sealed partial class CodexAppServerClient
{
    // This method is deliberately separate from ReadQuotaAsync. It never retries.
    public async Task<CreditRedemptionOutcome> ConsumeCreditAsync(CodexLaunchCommand command,
        string version, string creditId, string idempotencyKey, CancellationToken cancellationToken,
        string? expectedIdentity = null, bool requireIdentity = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(creditId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(CodexProtocol.TotalHardCeilingMs);
        ICodexProcess? process = null;
        var sent = new List<string>();
        var stderr = new StringBuilder();
        try
        {
            process = await StartAsync(command, bounded.Token).ConfigureAwait(false);
            _ = process.DrainStderrAsync(stderr, CodexProtocol.MaxStderrBytes, bounded.Token);
            await SendAsync(process, CodexProtocol.BuildInitialize(version), "initialize", sent, bounded.Token).ConfigureAwait(false);
            var init = await WaitForResponseAsync(process, "1", CodexProtocol.InitializeTimeoutMs, bounded.Token).ConfigureAwait(false);
            if (init.Status != CodexQuotaStatus.Available || init.Node?["result"] is not JsonObject
                || CodexProtocol.HasError(init.Node)) return CreditRedemptionOutcome.Unavailable;
            await SendAsync(process, CodexProtocol.BuildInitialized(), "initialized", sent, bounded.Token).ConfigureAwait(false);
            if (requireIdentity)
            {
                if (expectedIdentity is null) return CreditRedemptionOutcome.Unavailable;
                await SendAsync(process, CodexProtocol.BuildAccountRead(), "account/read", sent, bounded.Token).ConfigureAwait(false);
                var account = await WaitForResponseAsync(process, "2", CodexProtocol.AccountReadTimeoutMs, bounded.Token).ConfigureAwait(false);
                var identity = CodexAccountIdentity.Parse(account.Node);
                if (account.Status != CodexQuotaStatus.Available || identity.Status != CodexQuotaStatus.Available
                    || identity.Fingerprint != expectedIdentity) return CreditRedemptionOutcome.Unavailable;
            }
            var request = new JsonObject
            {
                ["id"] = 4, ["method"] = "account/rateLimitResetCredit/consume",
                ["params"] = new JsonObject { ["creditId"] = creditId, ["idempotencyKey"] = idempotencyKey }
            };
            await SendAsync(process, request.ToJsonString(), "account/rateLimitResetCredit/consume", sent, bounded.Token).ConfigureAwait(false);
            var response = await WaitForResponseAsync(process, "4", CodexProtocol.RateLimitsReadTimeoutMs, bounded.Token).ConfigureAwait(false);
            if (response.Status != CodexQuotaStatus.Available || CodexProtocol.HasError(response.Node))
                return CreditRedemptionOutcome.Unknown;
            return response.Node?["result"]?["outcome"]?.GetValue<string>() switch
            {
                "reset" => CreditRedemptionOutcome.Reset,
                "alreadyRedeemed" => CreditRedemptionOutcome.AlreadyRedeemed,
                "nothingToReset" => CreditRedemptionOutcome.NothingToReset,
                "noCredit" => CreditRedemptionOutcome.NoCredit,
                _ => CreditRedemptionOutcome.Unknown
            };
        }
        catch { return sent.Contains("account/rateLimitResetCredit/consume")
                ? CreditRedemptionOutcome.Unknown : CreditRedemptionOutcome.Unavailable; }
        finally
        {
            // Discard diagnostics from this mutating request: they can contain opaque credit IDs.
            if (process is not null)
                await CompleteAsync(CodexQuotaStatus.Unavailable, null, null, sent, new StringBuilder(), null, process).ConfigureAwait(false);
        }
    }
}
