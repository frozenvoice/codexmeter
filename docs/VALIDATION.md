# CodexMeter validation

## Current release — Codex only

- Release build: 0 warnings/errors; 890 tests passed, including shared-refresh ownership,
  waiter cancellation, retry after failure, unknown-vs-zero and cached identity-state tests.
- CI artifact-path parsing covers LF, CRLF, tab and space delimiters.
- Single-file Windows x64 self-contained publish verified: exactly `CodexMeter.exe`,
  with no browser extension, native host, WebView2 or external .NET runtime files.
- WPF flyout/settings rendered in dark and light themes using stored Codex quota metadata.
- Live standalone executable: refreshed successfully at 14:48:28 KST on 2026-09-07; server weekly usage 8%, remaining 92%. Chrome/Edge/Whale old native registrations absent; only CodexMeter process remained.
- Notification-icon replacement: production source/project guards pass and assembly reflection
  confirms retired taskbar strip/Win32 types are absent. No overlay is instantiated.
- Settings General/Widget/Connection pages rendered in Korean/English, dark/light, and compact
  sizing. Save/Cancel and disabled widget options checked through the WPF probe.
- Reset countdown boundaries, unknown/partial expiries, duplicate credit IDs, malformed
  counts, root-container precedence and old-cache loading covered by regression tests.
- Read-only live App Server check returned three available credit expiry timestamps;
  the bounded client reported child-process cleanup. Only projected quota metadata was used.
- Flyout renders reset countdowns and three credit expiry dates; exact times remain in tooltip.
- Sliding refresh bar removed. WPF probe confirms identical 339.91 DIP height before, during
  and after refresh for the same snapshot in dark/light and Korean/English; busy button stays disabled.
- Runtime checks: verify actual Codex refresh, no ChatGPT HTTP/SQLite collector startup,
  no native host registration, saved window position and tray/refresh behavior.
- A fresh PC requires installed and signed-in Codex CLI; no browser pairing is part of setup.

## Historical ChatGPT checks (retired; do not use as CodexMeter setup)
# Manual validation

ProMeter reconstructs usage from ChatGPT account history. These checks require a real signed-in Pro account. Live ChatGPT compatibility is not claimed until they pass.

## Companion pairing

1. Load the unpacked `extension/` in Chrome or Edge.
2. Register the Chrome/Edge native host from Settings with that extension ID.
3. Sign in to ChatGPT in that normal browser. Do not use WebView2 for Google/Microsoft/Apple.
4. Click **Connect to ProMeter** in the extension popup.
5. Run the first manual sync. Sign-in alone must not scan history.

## Multi-device

1. On this PC, open the tray flyout and note the GPT Pro count.
2. On the Android ChatGPT app, send one GPT-5.6 Sol Pro or GPT-6 Pro message.
3. On this PC, choose **Sync now**.
4. Confirm the Pro counter increases by 1.

## Another PC

1. Use ChatGPT on a second Windows PC with the same account.
2. Sync on the machine running ProMeter.
3. Confirm the remote usage appears.

## Reasoning

1. In ChatGPT, send one GPT-5.6 Sol message with Extra High (`매우 높음`).
2. Sync.
3. Confirm **SOL REASONING → Extra High** increases by 1.
4. Confirm the Pro meter does **not** increase for non-Pro Sol reasoning.

## Regenerate

1. Send one Pro message.
2. Regenerate the response.
3. Sync.
4. If the conversation endpoint returns every branch `request_id`, the Pro count should increase by 1 for the regenerate.
5. If only the current branch is returned, Coverage should stay Estimated/Good and this limitation should remain visible.

## Archive

1. Use Pro in a chat, then archive that chat.
2. Sync.
3. Confirm the current-period Pro count still includes that usage.

## Project

1. Send a Pro message inside a ChatGPT Project.
2. Sync.
3. Confirm the event appears and Coverage lists Projects as available.

## Temporary / deleted

1. Use Temporary Chat or delete a conversation.
2. Confirm those turns do **not** appear after sync.
3. Coverage should continue to list Temporary and Deleted as unavailable.

## Official export

1. From ChatGPT Settings → Data Controls, download `conversations.json` if available.
2. Import it from ProMeter Settings.
3. Import the same file again.
4. Confirm usage counts do not double.

## Theme readability (Dark and Light)

Check each screen in Dark, then repeat in Light (Settings → Theme):

- Main
- Settings
- Flyout
- Coverage
- Welcome
- About

Confirm labels, text boxes, combo boxes and dropdown items, checkboxes, tab headers, DataGrid text/headers, and flyout values stay readable. No black/default text on a dark background, and no washed-out light text on a light background.

## Browser companion with VPN left on

Do not disable the browser VPN or proxy, switch browsers, or weaken browser security.

1. Keep the VPN/proxy enabled in Edge or Chrome.
2. Confirm chatgpt.com itself loads and is signed in in a normal tab.
3. Confirm the companion popup shows connected to ProMeter.
4. Leave that ChatGPT tab open (or let ProMeter open https://chatgpt.com/ and sign in there, then retry).
5. Run a manual sync.
6. Confirm ProMeter does **not** tell you to disable VPN.
7. If ChatGPT returns 403, the UI/log should say `ChatGPT rejected the page request (403)`, not `ChatGPT session expired`, unless the ChatGPT tab is independently signed out.
8. If no ChatGPT tab exists, the status should be `Open/sign in to ChatGPT, then retry`.
9. If MAIN-world page execution is blocked, the status should be `ChatGPT page bridge unavailable` rather than a silent service-worker fetch.


## Browser page timeout recovery

`node extension/test-companion.js` includes synthetic regressions for a hung session fetch,
response body, backend body, and 401 refresh; shared authentication recovery; late session
completion; injection before document idle; frozen/discarded tab preference; and late Chrome callbacks.
No real account content or credentials are used in these tests.

Live check after reloading the unpacked Edge extension:

1. Activate an already signed-in ChatGPT tab, then run ProMeter's full manual history sync.
2. Confirm account/index requests resume and the completed reconstruction is displayed.
3. Repeat with a background ChatGPT tab and, separately, an Edge sleeping tab. The companion
   should activate the same sleeping tab once and resume. It must not focus the browser window,
   reload the user's conversation, disable VPN, or reset stored usage.
4. Confirm stalled requests fail within the documented deadline and a later retry is not held by
   the previous session promise. `Connection required` can also reflect a previous BridgeTimeout;
   inspect the safe sync failure category rather than treating that label as proof of logout.

Live result on 2026-09-07: after the user reloaded/reconnected the fixed-ID Edge companion,
manual history sync completed at 13:28:36 +09:00 with `UpToDate`, `coverage=Estimated`,
and zero failed conversations. This verifies history transport recovery, not an authoritative
remaining-quota count. Subsequent 13:37–13:38 retries failed at page preparation, so this earlier success alone did not establish a complete fix. Version 5 adds bounded same-tab wake-up recovery; verify this with the deployed version.


## Unknown-reset Monday regression

- With no configured/confirmed reset, weekend Pro observations must remain in **Last 7 days reconstructed**
  after Monday midnight. Never turn the default Monday preference into a quota reset.
- No next-reset date is displayed until there is an applicable anchor. Server counts and configured
  reset periods still follow their original rules; local-calendar Sol analytics are unchanged.
- `RollingProHistoryTests` uses synthetic weekend, too-old, and future requests to verify the boundary.

- Reset lookup fallback regression: empty init followed by model-limit metadata must recover reset timing; an ordinary model catalog must never manufacture a reset. Bridge version 6 requires extension Reload; live reset retrieval remains unverified.

## Cross-PC verification, 2026-09-07

- After extension Reload, live sync completed at 14:17:43 KST: UpToDate / Estimated,
  34 loaded conversations, 0 failed; archived and 9 project indexes were checked.
- Current local server-status metadata still has no model limits or retained reset.
  Provider fallback did not recover a reset from this live response.
- The user-referenced home conversation records a prior reset around
  2026-09-06 14:20:14 KST and a reconstructed count of 12. The attached log examined
  here did not independently establish that reset timestamp.
- A read-only production QuotaEngine calculation over the freshly synchronized local
  metadata produces 12 with that historical-chat anchor, versus 72 in the rolling
  seven-day window. No server-status/settings values were overwritten from chat text.
- Account-history collection supports cross-device reconstruction; identical current-cycle
  displays on fresh PCs remain unverified until the same evidenced reset boundary is
  available on each PC. A successful scan does not establish authoritative billed usage.
