using System.Collections.Concurrent;
using System.Diagnostics;
using CycleArc.Codex;
using CycleArc.Services;

namespace CycleArc.Providers.Claude;

public sealed record ClaudeConnectionOverview(ClaudeConnectionBinding? Binding, ClaudeAuthentication Authentication,
    bool Installed, string ConfigDirectory);
public sealed record ClaudeConnectionResult(bool Success, ClaudeAuthentication Authentication,
    ClaudeConnectionBinding? Binding = null, ClaudeSetupFailure? Failure = null);

public interface IClaudeConnectionActions
{
    Task<ClaudeConnectionOverview> InspectAsync(string profileId, CancellationToken token);
    Task<ClaudeConnectionResult> ConnectAsync(string profileId, string executable, bool login, string? directory, CancellationToken token);
    Task DisconnectAsync(string profileId, CancellationToken token);
    void OpenClaude(string profileId, string workingDirectory);
}

public sealed class ClaudeConnectionService(CodexAccountStore accounts, IClaudeCli? cli = null, IClock? clock = null) : IClaudeConnectionActions
{
    private readonly IClaudeCli _cli = cli ?? new ClaudeCli();
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, ClaudeAuthentication> _identities = new(StringComparer.Ordinal);

    public string? Email(string profileId, string? fingerprint) => fingerprint is not null
        && _identities.TryGetValue(profileId, out var auth) && auth.Status == ClaudeAuthStatus.SignedIn
        && auth.Fingerprint == fingerprint ? auth.Email : null;

    public async Task<ClaudeConnectionOverview> InspectAsync(string profileId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await InspectCoreAsync(profileId, token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ClaudeConnectionOverview> InspectCoreAsync(string profileId, CancellationToken token)
    {
        RequireProfile(profileId);
        var read = new ClaudeConnectionStore(accounts, profileId).Read();
        if (read.Unavailable) throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
        var binding = read.Binding;
        var directory = binding?.ConfigDirectory ?? ClaudeConnectionPaths.DefaultDirectory;
        var useDefault = binding?.UseDefaultConfig ?? ClaudeConnectionPaths.UsesImplicitDirectory;
        var executable = binding is not null && File.Exists(binding.CliExecutable) ? binding.CliExecutable : _cli.FindExecutable();
        var auth = executable is null ? new ClaudeAuthentication(ClaudeAuthStatus.NotInstalled)
            : await _cli.AuthenticateAsync(executable, useDefault ? null : directory, false, token).ConfigureAwait(false);
        _identities[profileId] = auth;
        return new(binding, auth, binding is not null && ClaudeStatusLineInstaller.IsInstalled(directory, profileId), directory);
    }

    public async Task<ClaudeConnectionResult> ConnectAsync(string profileId, string executable, bool login, string? directory, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            RequireProfile(profileId);
            var store = new ClaudeConnectionStore(accounts, profileId);
            var read = store.Read();
            if (read.Unavailable) return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.ConnectionUnavailable);
            var previous = read.Binding;
            var useDefault = !login && directory is null && (previous?.UseDefaultConfig ?? ClaudeConnectionPaths.UsesImplicitDirectory);
            var managedRoot = accounts.ManagedClaudeDirectory(profileId);
            var target = ClaudeConnectionPaths.Normalize(directory ?? (login
                ? Path.Combine(managedRoot, Guid.NewGuid().ToString("N"))
                : previous?.ConfigDirectory ?? ClaudeConnectionPaths.DefaultDirectory));
            if (target is null) return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.InvalidSettings);
            var cliPath = previous is not null && File.Exists(previous.CliExecutable) ? previous.CliExecutable : _cli.FindExecutable();
            if (cliPath is null) return new(false, new(ClaudeAuthStatus.NotInstalled));
            var auth = await _cli.AuthenticateAsync(cliPath, useDefault ? null : target, login, token).ConfigureAwait(false);
            _identities[profileId] = auth;
            if (auth.Status != ClaudeAuthStatus.SignedIn) return new(false, auth);
            RequireProfile(profileId);
            token.ThrowIfCancellationRequested();
            var same = previous is not null && string.Equals(previous.ConfigDirectory, target, StringComparison.OrdinalIgnoreCase)
                && previous.IdentityFingerprint == auth.Fingerprint && previous.UseDefaultConfig == useDefault;
            var binding = new ClaudeConnectionBinding(1, profileId, target, cliPath,
                target.Equals(managedRoot, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(managedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                auth.Fingerprint!, same ? previous!.ConnectedAt : _clock.UtcNow, useDefault);
            store.Save(binding);
            try
            {
                await ClaudeStatusLineInstaller.InstallAsync(accounts, profileId, target, executable, token).ConfigureAwait(false);
            }
            catch
            {
                if (previous is null) store.Delete(); else store.Save(previous);
                throw;
            }
            if (previous is not null && !string.Equals(previous.ConfigDirectory, target, StringComparison.OrdinalIgnoreCase))
            {
                try { await ClaudeStatusLineInstaller.RestoreAsync(previous.ConfigDirectory, profileId, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ClaudeSetupException or OperationCanceledException)
                {
                    // The committed new binding is usable. An old wrapper left in a locked or
                    // edited settings file still preserves its original output, but the bridge
                    // rejects its old config directory and can no longer collect for this profile.
                }
            }
            // A new binding never reuses a quota sample from the previous account/configuration.
            return new(true, auth, binding);
        }
        catch (ClaudeSetupException ex) { return new(false, new(ClaudeAuthStatus.Failed), Failure: ex.Failure); }
        catch (OperationCanceledException) { return new(false, new(ClaudeAuthStatus.Cancelled)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, new(ClaudeAuthStatus.Failed), Failure: ClaudeSetupFailure.ConnectionUnavailable); }
        finally { _gate.Release(); }
    }

    public async Task DisconnectAsync(string profileId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var store = new ClaudeConnectionStore(accounts, profileId);
            var read = store.Read();
            if (read.Unavailable) throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
            if (read.Binding is not { } binding) return;
            await ClaudeStatusLineInstaller.RestoreAsync(binding.ConfigDirectory, profileId, token).ConfigureAwait(false);
            await new ClaudeStatusLineStore(accounts.ClaudeStatusLinePath(profileId), profileId)
                .RecordAsync(new ClaudeStatusLineResult(ClaudeInputStatus.Missing), _clock.UtcNow, token).ConfigureAwait(false);
            store.Delete();
            _identities.TryRemove(profileId, out _);
        }
        finally { _gate.Release(); }
    }

    public void OpenClaude(string profileId, string workingDirectory)
    {
        RequireProfile(profileId);
        var binding = new ClaudeConnectionStore(accounts, profileId).Read().Binding
            ?? throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
        if (!Directory.Exists(workingDirectory) || !File.Exists(binding.CliExecutable))
            throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
        var start = ClaudeCli.StartInfo(binding.CliExecutable, binding.UseDefaultConfig ? null : binding.ConfigDirectory, false);
        // Explicit Open Claude Code action: an interactive terminal in the folder selected by the user.
        start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        start.Environment["CYCLEARC_CLAUDE_LAUNCH"] = binding.CliExecutable;
        start.Arguments = "/d /v:off /s /k \"\"%CYCLEARC_CLAUDE_LAUNCH%\"\"";
        start.CreateNoWindow = false;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = false;
        start.StandardInputEncoding = start.StandardOutputEncoding = start.StandardErrorEncoding = null;
        start.WorkingDirectory = workingDirectory;
        Process.Start(start)?.Dispose();
    }

    private void RequireProfile(string profileId)
    {
        if (!accounts.ContainsClaude(profileId)) throw new ClaudeSetupException(ClaudeSetupFailure.ConnectionUnavailable);
    }
}
