# CodexMeter architecture

## Active product — Codex only (2026-09-07)

`CodexMeter.exe` → shared `CodexRefreshCoordinator` → `CodexQuotaService` →
installed, signed-in Codex CLI (`app-server --stdio`) → account/rate-limit metadata.

- Tray, flyout, widget and timer all share one bounded refresh. Cancelling a
  waiting caller does not cancel the active owner's work. File/process work runs off the UI thread.
- Only Codex is visible: actual provided periods, used/remaining percentages, reset times,
  reset-credit metadata and last refresh. Signed-out/missing CLI states do not display cached
  percentages as current; stale data is explicitly marked.
- The desktop never constructs ChatGPT transports, collectors, SQLite stores, pairing servers
  or Pro reset services. Retired WPF views and WebView2 are excluded from its build.
- Publish bundles the .NET runtime and produces exactly one `CodexMeter.exe`; Codex CLI itself
  remains an external installed/sign-in prerequisite. No extension or companion host is shipped.
- Existing settings/cache paths are retained; history is neither read nor deleted.
  Startup removes only known native-host registrations matching the old owned manifest path.
  The browser extension must be removed via the browser's extension manager.
- Repository and local checkout are named `codexmeter`; internal namespaces/solution name have not yet been renamed from ProMeter.
- The internal rename is incomplete: `src/ProMeter` is the active WPF app,
  `src/ProMeter.Core` contains active Codex and retained legacy logic, and
  `src/ProMeter.CompanionHost` is a retired host still built by the solution for legacy tests,
  but never published with CodexMeter. Source/project names can be migrated independently
  of the persisted `%LOCALAPPDATA%\ProMeter` compatibility path.
- The retired `extension/` source and its CI validation step have been removed. Extension-only
  file consistency tests were removed; retained .NET transport and manifest fixture tests remain.
  Previous extension sources are available in Git history.
- Windows owns the notification icon's allocated slot through NotifyIcon. The retired taskbar
  overlay and TaskbarWin32 are excluded from the desktop build, with no geometry/fullscreen
  polling. Its old JSON settings remain compatible but startup/save disable the overlay.
- Settings has General, Widget and Connection tabs with themed switches, sliders and explicit
  Save/Cancel behavior. Korean/English and dark/light themes use the existing resources.
- Reset rows show server reset time plus remaining days/hours. A one-minute UI-only timer
  refreshes countdowns; account refresh remains every five minutes.
- Root reset-credit metadata takes precedence as one container. Available Codex reset credits
  are deduplicated transiently by ID and projected to nullable expiry timestamps only.
  The additive cache field preserves older snapshots; missing/partial expiries stay explicit.
  The separate reset-credit card shows every date group in a bounded scrolling list, with exact times in tooltips.
- Flyout follows the supplied two-card layout: large usage ring, separated quota rows, and a
  reset-credit list below. Its 440 DIP width and scrollable body fit the current work area.
  Fresh/refreshing/stale/identity-failure states stay distinct; credit unknown is never zero.
- The widget context menu includes Close widget, which saves its disabled preference and hides
  only the widget. Settings can re-enable it; the tray and app remain running.
- Widget drag uses captured screen-coordinate deltas, independent of the moving window.
  Crossing the movement threshold suppresses click-to-open; release or capture loss emits
  one position-save event. Click-through remains an explicit setting and disables input.
- The final supplied sample removes the redundant ring legend and places the credit count in
  a badge beside its title. The badge opens help; the right chevron toggles the expiry list.
  Expansion defaults on, survives refresh, and keeps a 108 DIP list viewport for 1+ date groups.
- Reset-credit help is click-toggled on one reused tooltip instance. Refresh keeps its state;
  hiding the detail window closes it. Automatic hover opening is disabled for this button.
- Flyout refresh uses only the header status and fixed-size spinning button; no sliding progress
  bar or duplicate in-card refreshing message changes the card height.
- Flyout header exposes refresh, settings, pin and close. Settings is owned by the visible
  flyout so it stays above a pinned card. Ring captions omit the product prefix; the old
  accuracy badge is removed. Widget-only options are disabled when the widget is off.
- Cleanup audit: old WPF views/WebView2/companion are excluded from desktop build, but legacy
  Core logic, SQLite package, compatibility settings fields and regression tests remain.
  Removing that shared legacy layer requires a separate source/project split; none runs as
  a CodexMeter history collector.

## Historical ProMeter architecture (retired)

The following describes retained legacy code and prior investigations. It is not the
runtime or setup contract for CodexMeter.
# ProMeter architecture

ProMeter reconstructs ChatGPT Pro usage from **account conversation history**, not from local request interception. History is the reconstruction input. Only a matching server quota counter is authoritative. That is what allows company PC, home PC, and mobile usage to share one meter.

```text
Browser companion (recommended)
  Chrome/Edge tab
    → MV3 extension (operation allowlist, page-local auth, endpoint projection)
    → Native Messaging full-duplex (stdin↔pipe and pipe↔stdout pumps)
    → prometer-companion-host.exe
    → named pipe ProMeterCompanion (CurrentUserOnly, serialized writer)
    → CompanionRequestHub (timeout, generation, FailAllPending)
    → BrowserCompanionTransport (logical operations only)

WebView2 fallback (only when that sign-in works)
  WebView2 session → SessionAuthCoordinator → WebViewTransport

Data Export (non-real-time)
  conversations.json  →  ConversationExportImporter

All three keep the same ChatGptProvider → SyncEngine → SQLite → QuotaEngine path.
Transports are never swapped silently.
```

## Layers

- `ProMeter.Core` — models, ChatGPT provider abstraction, parsers, SQLite, quota, import/export
- `ProMeter` — WPF tray app, WebView2 fallback login, companion pipe server, notifications, startup
- `ProMeter.CompanionHost` — Chrome/Edge native messaging host (`prometer-companion-host.exe`)
- Former `extension/` (Git history only) — Manifest V3 companion. `https://chatgpt.com/*` only. No `cookies` permission.

## Provider abstraction

`IChatGptProvider` is the only business-facing ChatGPT API. Endpoint strings live in `ChatGptEndpoints`. If ChatGPT changes paths, only the provider/endpoints change.

Observed 2026 web paths (same-origin from chatgpt.com):

- `GET /api/auth/session`
- `GET /backend-api/me`
- `GET /backend-api/accounts/check/v4-2023-04-27`
- `GET /backend-api/models`
- `GET /backend-api/conversations`
- `GET /backend-api/conversation/{id}`
- `POST /backend-api/conversation/init` (quota metadata, if present)
- `GET /backend-api/gizmos/snorlax/sidebar`
- `GET /backend-api/gizmos/{id}/conversations`

These are unofficial internal endpoints the ChatGPT website itself uses. They can change without notice. Programmatic history access is unsupported and may carry account or terms risk. Official ChatGPT Data Export remains the lower-risk, non-real-time fallback.

## Authentication

Social login must use a normal Chrome or Edge session plus the browser companion. Embedded WebView OAuth for Google, Microsoft, and Apple is unsupported. ProMeter does not spoof a user agent.

The companion security model:

- Native Messaging is initiated by the unpacked extension (`com.prometer.bridge`).
- The host binds only to the current-user named pipe `ProMeterCompanion` on loopback.
- A local pairing token authenticates host messages. It is not a ChatGPT secret.
- Native Messaging is two concurrent pumps. Application-initiated invoke messages do not wait for another extension message.
- The extension constructs method/path from a closed operation enum. Arbitrary `/backend-api` fetch is forbidden.
- Responses are projected through endpoint-specific metadata allowlists before leaving the browser. Access tokens stay in authenticated chatgpt.com page-local memory only.
- Cookie databases are never read. Cookies and access tokens are never sent to the Windows app.

WebView2 remains an optional fallback for authentication methods that actually work there. Its profile lives under `%LOCALAPPDATA%\ProMeter\webview`. Access tokens stay in process memory only.

Backend fetches validate both the current HTTPS ChatGPT origin and the requested relative target before credentials or JavaScript are attached.

## Deduping

One `request_id` is one usage event, even when hidden reasoning, tool calls, and the final assistant message share that id. If `request_id` is missing, ProMeter falls back to conversation + message id.

All mapping nodes are scanned, so regenerate branches are counted when the server returns them.

## Privacy

Usage events store model, timestamps, request/message ids, and source metadata only. Prompt and response text are discarded.


## Browser page request deadlines

- The page bridge runs immediately rather than waiting for `document_idle`; its modules do not
  depend on DOM readiness. Exact HTTPS origin and operation allowlists still apply.
- Runnable ChatGPT tabs take priority over frozen/discarded tabs. A suspended tab, or a tab
  whose bridge probe times out, is activated once in its own window before retrying preparation.
  This can select the ChatGPT tab but does not focus the browser window, explicitly reload/navigate
  a conversation, change VPN settings, switch accounts/tabs on fetch failure, or change transports.
- Tab query, bridge probe, and injection have bounded setup waits. The extension invocation has
  a 55-second ceiling, below the native hub's existing 60-second deadline.
- Page fetches and response-body reads share a 40-second operation deadline and AbortSignal.
  A stalled session probe releases single-flight waiters so retry can authenticate again;
  late responses from timed-out probes cannot overwrite recovered page-local authentication.
- PAGE_BRIDGE_VERSION 5 replaces the prior unbounded page executor after extension reload.
  Conversation endpoint/whole-conversation budgets are unchanged.

## Unknown Pro reset

When neither an applicable server/retained anchor nor a user-configured anchor exists, Pro
reconstruction and history scanning use the rolling interval (now - 7 days, now]. It is labelled
"Last 7 days reconstructed" and has no next-reset timestamp. It must never be described as an
actual quota cycle, exact remaining amount, or lower bound. Confirmed quota periods and Sol
local-calendar day/week analytics retain their existing rules.

- When init has no usable Pro metadata, quota lookup also checks models metadata. The models projection preserves only the existing scalar quota allowlist. Confirmed-reset cache is local to each PC; conversation sync does not transfer it.
