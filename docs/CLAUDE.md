# Claude Code statusLine provider

CycleArc reads the [official Claude Code statusLine JSON](https://code.claude.com/docs/en/statusline), delivered to a configured command on stdin. It projects only:

| Official field | CycleArc meaning |
| --- | --- |
| `rate_limits.five_hour.used_percentage` | Five-hour usage, 0–100, retaining fractional values |
| `rate_limits.five_hour.resets_at` | Five-hour reset as Unix epoch seconds |
| `rate_limits.seven_day.used_percentage` | Weekly usage, 0–100, retaining fractional values |
| `rate_limits.seven_day.resets_at` | Weekly reset as Unix epoch seconds |

Claude Code may omit each window independently, including before a first response or for an unsupported plan. Missing data stays unknown; a malformed present window is a schema failure rather than a successful partial reading. The model/context-token fields are not used as account quota.

## Connect a profile

In **Manage accounts → Add an account · Connection guide**, create a **Claude profile** with an optional local nickname. Open **Connect**, copy its statusLine settings, and merge that entry into the Claude Code settings used for the intended account. The JSON points to the current `CycleArc.exe`; reconnect if you move the executable. CycleArc never edits Claude settings automatically.

The generated command invokes a headless mode of the same executable:

```text
CycleArc.exe --claude-statusline <local-profile-id>
```

It reads JSON on stdin and emits a short quota line, for example `Claude | 5h 23.5% | 7d 41.2%`. On Windows, the generated JSON uses an encoded PowerShell command so paths containing spaces and shell metacharacters work through either Git Bash or PowerShell. The encoding contains a static invocation, not credentials; it sets UTF-8 input and passes stdin to the executable in a pipeline. There is no extra shipped companion executable or PowerShell dependency beyond Windows PowerShell.

**Account attribution:** statusLine has no email or account identifier. CycleArc cannot discover or verify the emitting Claude account. Each command's explicit local profile ID determines where its data goes. Use separate profiles/settings scopes for separate Claude accounts and change the command when switching sign-ins. Do not feed different accounts to the same profile. Aliases use the same trim/control-character removal/80-character limit as Codex; without an alias Claude displays its provider and a short local ID. No email is guessed from a directory, session or credential file.

## Preserve an existing statusLine

Claude Code runs one configured statusLine command. Pasting the generated JSON replaces that one entry, so merge a wrapper when an existing statusLine must remain. For an existing PowerShell script, the following example supplies the same official JSON to both and keeps the existing display output:

```powershell
# This wrapper is the command configured in Claude Code, not a background poller.
[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::InputEncoding
$statusJson = $input | Out-String
$statusJson | & 'C:/Apps/CycleArc/CycleArc.exe' --claude-statusline 'YOUR_PROFILE_ID' | Out-Null
$statusJson | & 'C:/Users/YOU/.claude/existing-statusline.ps1'
```

Use your actual executable, generated profile ID and existing script path. A `statusLine.command` such as `powershell.exe -NoProfile -File "C:/Apps/CycleArc/statusline-wrapper.ps1"` invokes the wrapper. It holds the input in memory only; do not write it to a file or log. Adapt the second invocation if the existing command uses a different interpreter.

## Freshness and persistence

- A sample is fresh for less than five minutes after CycleArc receives valid input, provided no included reset timestamp has passed. Receipt time is local observation metadata, not an authoritative source timestamp.
- Missing/malformed input preserves the last valid sample as stale. A later valid callback recovers the profile. A valid one-window callback replaces the previous sample; it does not carry an absent older window into fresh data.
- The provider checks its small projected inbox every two seconds. The Codex refresh schedule remains independent. Neither this poll nor manual refresh renews receipt time, invokes Claude, reads authentication data or generates a model response.
- After five minutes without valid input, at an elapsed reset, or after clock rollback makes a sample future-dated, the last values remain visible as stale. CycleArc never rolls percentages back to zero locally. The state survives application restarts.
- Each profile stores `accounts/<local-id>/claude-statusline.json` under the existing `%LOCALAPPDATA%/ProMeter` directory. Only provider/profile ID, projected windows, receipt timestamps and a bounded status enum are serialized. No transcript path, session ID, prompt, response, email or token is retained.
- Independent callbacks use a bounded exclusive lock, monotonic receipt ordering, an atomic replace and a previous-good backup. Interrupted writes leave the previous cache intact. Corrupt primary data can use the backup with a stale label.
- The input limit is 256 KiB with a five-second receiver deadline. Present percentages must be finite numbers in 0–100 and reset timestamps must be representable positive integer Unix seconds. Unknown unrelated JSON fields are ignored and never copied to storage or output.

Account registry version 1 continues to load as Codex. Adding a Claude profile upgrades the registry and its previous-good backup to version 2; older builds refuse both instead of falling back to a v1 account list or interpreting Claude profiles as Codex credentials. The backup retains its previous profiles. Codex home references, selected IDs, aliases, order, ignored homes and quota-cache paths are preserved.

## Validation

Unit checks exercise synthetic official shapes, missing windows, malformed fields, fractional usage, stale/recovery/reset boundaries, cache backup and concurrent receipt ordering, provider separation and legacy registry compatibility. Production WPF checks cover mixed accounts in both languages and all themes, provider labels and aliases, stale values, compact connection guidance and Codex-only credit controls.

Both `dev-run.ps1` and Windows CI validate the receiver in the built application and again in the single-file published executable. A held desktop mutex ensures the headless path works beside the tray application. Synthetic checks use an explicit collector-only `--data-root <absolute-temporary-directory>` and registered fixture profiles, so they never touch actual account settings. Direct stdin, generated PowerShell, Git Bash when installed, malformed input and missing-stdin deadlines are checked. No live Claude authentication or usage session is required for these checks.
