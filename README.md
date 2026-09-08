# CodexMeter

**Your Codex limits, one click away.**

A native Windows tray app for checking Codex usage, remaining percentages, reset times, and reset credits—without opening a terminal.

> **Pro subscriptions only.** This release supports Codex usage monitoring for ChatGPT Pro subscribers. **The ChatGPT Plus five-hour usage limit is not supported.**

[![Windows build](https://github.com/frozenvoice/codexmeter/actions/workflows/windows.yml/badge.svg)](https://github.com/frozenvoice/codexmeter/actions/workflows/windows.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows_x64-0078D4.svg)](#get-started)

[Download for Windows](https://github.com/frozenvoice/codexmeter/releases/latest) · [한국어](docs/README.ko.md) · [Report an issue](https://github.com/frozenvoice/codexmeter/issues)

<table>
  <tr>
    <td align="center"><strong>Dark</strong></td>
    <td align="center"><strong>Light</strong></td>
  </tr>
  <tr>
    <td><img src="docs/images/overview-dark.png" alt="English dark view showing weekly usage, remaining percentage, reset countdown, and reset credits" width="440"></td>
    <td><img src="docs/images/overview-light.png" alt="The same Codex usage view in the light theme" width="440"></td>
  </tr>
</table>

*English previews rendered from the production WPF views using sample quota data. Available windows and reset-credit details depend on what your account reports.*

## At a glance

- **Usage in the tray.** A Windows notification-area icon keeps the meter within reach. Click for the detailed card; pin it to keep it visible.
- **Clear quota windows.** See Pro account usage and remaining percentages, the server's reset time, and a countdown for the windows reported by the server.
- **Reset credits.** View the available count and expiry times when the server supplies them. Use an individual reset after confirmation. Missing expiry information stays explicitly unknown.
- **Optional desktop widget.** A compact, draggable meter with adjustable opacity, always-on-top, and click-through options. Off-screen positions recover automatically.
- **Your preferred appearance.** Dark, Light, or live System theme; English and Korean; keyboard zoom from 80% to 150%.
- **Honest refresh states.** Automatic checks every five minutes, visible manual-refresh progress, and distinct fresh, stale, signed-out, and unavailable states.

## Get started

**Requirements:** A ChatGPT Pro subscription, Windows 10/11 on x64, an installed Codex CLI already signed in to your account, and network access for quota checks.

1. Download **`CodexMeter.exe`** from the [latest release](https://github.com/frozenvoice/codexmeter/releases/latest).
2. Put it in a folder you want to keep and run it. The .NET runtime is bundled; there is no separate runtime installer.
3. Open the tray icon to inspect your limits. If Codex cannot be found, open **Settings → Connection** and select its executable path.

CodexMeter discovers `codex.exe` or `codex.cmd` through PATH and supported installation locations. The Codex CLI itself is not bundled. Sign-in remains managed by Codex.

The first launch opens the detail card. Later launches start in the tray; `CodexMeter.exe --show` opens the card at startup. If Windows hides the tray icon, move it out of the notification-area overflow. Starting with Windows and showing the desktop widget are optional settings.

### Controls

| Action | Result |
| --- | --- |
| Click the tray icon | Open or hide the detail card |
| Refresh button | Check account limits immediately |
| Pin button | Keep the detail card on top |
| `Ctrl` + `+` / `Ctrl` + `-` | Enlarge or reduce the detail card |
| `Ctrl` + `0` | Restore 100% zoom |
| Drag the widget | Move it and save its position |
| Right-click the widget | Open its menu, including Close widget |

The zoom shortcuts also support the numeric keypad. Widget position can be reset from **Settings → Widget**; the reset takes effect when you save.

<details>
<summary><strong>Settings and compact widget</strong></summary>

<p><img src="docs/images/settings.png" alt="English settings with theme, language, tray style, and optional Windows startup" width="640"></p>
<p><img src="docs/images/widget.png" alt="Compact desktop widget showing sample weekly usage" width="220"></p>

</details>

## How it works

CodexMeter starts a bounded, short-lived **Codex App Server** process and requests account/rate-limit metadata. Every UI entry point shares the same refresh operation. It does not run a model turn to measure usage.

Percentages come from the reported limit windows. CodexMeter does **not** turn them into invented request counts or combine unrelated reset periods. When a refresh fails, the last valid snapshot may remain visible with a stale label. Opening the card immediately after a failed check does not trigger repeated automatic retries; manual refresh remains available.

Countdowns and “last checked” ages update locally once a minute without another server request.

### Privacy

- No prompt, response, conversation, project, or rollout collection.
- No direct reading or copying of Codex authentication files, tokens, browser cookies, or credentials.
- No external telemetry or analytics.
- Only local preferences, projected quota metadata, and safe diagnostic logs are retained. Authentication is handled by the installed Codex process.

Settings and quota cache remain under `%LOCALAPPDATA%\ProMeter` for upgrade compatibility. Settings use atomic replacement with a previous-good backup and recovery if the primary file is damaged.

CodexMeter is an independent project and is not affiliated with or endorsed by OpenAI. Compatibility depends on the installed Codex App Server protocol and the metadata available to your account.

## Build from source

Requires Windows, PowerShell 7, and the .NET 8 SDK.

```powershell
git clone https://github.com/frozenvoice/codexmeter.git
cd codexmeter
.\dev-run.ps1
```

The launcher restores dependencies, builds Release, runs the tests and WPF checks, then publishes and launches **one self-contained `CodexMeter.exe`**. It retries transient deployment locks and restores the previous local build if replacement or startup fails.

- `-NoLaunch`: validate the staged executable without replacing the running local app.
- `-Fast`: skip the unit suite only when it has already passed for the same changes.
- CI also publishes the single executable as the `CodexMeter-win-x64` artifact.

| Path | Purpose |
| --- | --- |
| `src/CodexMeter` | Active WPF tray application |
| `src/CodexMeter.Core` | Quota protocol, presentation logic, persistence, and retained legacy logic |
| `tests/CodexMeter.Tests` | Unit and regression tests |
| `tests/CodexMeter.UiSmoke` | Production WPF layout checks and documentation previews |

See [Architecture](docs/ARCHITECTURE.md), [Validation](docs/VALIDATION.md), and [preview generation](docs/images/README.md) for implementation and verification details.

## Upgrading from ProMeter

Run `CodexMeter.exe` instead of `prometer.exe`. Existing preferences and quota-cache paths remain compatible. The former ChatGPT history reconstruction, browser companion, and WebView2 features are retired; old history data is neither read nor deleted by the active app. The old browser extension can be removed through your browser's extension manager.

Retained legacy source and tests are identified in the architecture document. The companion host and retired screens are excluded from the shipped executable.

## License

[MIT](LICENSE). Contributions and reproducible bug reports are welcome. Please omit account credentials and conversation content from issues and attachments.
