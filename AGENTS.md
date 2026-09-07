# CodexMeter agent instructions

Repository/solution: `prometer` / `ProMeter.sln`. Product: **CodexMeter**.
Windows-only .NET 8 WPF tray app, distributed as one `CodexMeter.exe`.

## Active product direction

- Monitor Codex account limits through the installed, signed-in Codex App Server protocol.
- No ChatGPT Pro/Sol counters, browser extension, WebView2, conversation sync or active SQLite collector.
- Keep Codex unavailable/stale/fresh states truthful; never fabricate counts from percentages.
- Keep the tray, optional widget/taskbar, theme/language settings, bounded shared refresh,
  existing user preferences and quota-cache compatibility.
- Publish exactly one self-contained `CodexMeter.exe`; do not ship a companion host or extension.
- Read `README.md` and the active sections of `docs/ARCHITECTURE.md` / `docs/VALIDATION.md`.
- Browser/ChatGPT-specific rules below apply only when maintaining the retained legacy source/tests.
  They do not authorize re-enabling those features or require installing/reloading an extension
  for the current product. Active UI must not show Pro reconstruction.
## Correctness

- Never convert an unknown or malformed API shape into a successful empty result.
- Never silently undercount.
- Schema mismatch must be surfaced (`ProviderSchemaMismatch` / coverage incomplete).
- Incomplete pagination must reduce coverage.
- Failed or incomplete scans must never advance the successful watermark.
- Missing `update_time` must never become a trusted incremental watermark.
- Fatal 401/403, 429, and offline conditions must abort the entire sync. Do not record remaining conversations as failed after a global auth/rate-limit/offline error.
- Quota windows with different reset periods must never be mixed. Never use a daily reset to compute the seven-day reconstruction period.
- Ambiguous quota metadata must remain diagnostic-only.
- Reconstructed counts must never be labeled Authoritative.
- Conversation-history reconstruction is never an authoritative remaining-quota count unless the server explicitly supplies authoritative used/remaining metadata.
- A locally configured plan limit must never be combined with reconstructed usage to present an exact "remaining" count.
- Server model-limit reset metadata may be authoritative for reset timing even when server used/remaining counts are unavailable.
- Count confidence and reset confidence are separate concepts.
- blocked_features metadata may be correlated with Pro model-limit reset metadata, but correlation must not be presented as an explicit server relationship unless the server actually provides one.
- A null block_reason must not be converted into "quota exhausted", "safeguard", or another invented cause.
- Every visible UI surface must use the same Pro status presentation semantics: tray, Flyout, Main window, taskbar strip, floating widget, tooltip and notifications.
- Reconstructed counts must be labelled as reconstructed observed requests, not as billed quota. Use `N+` / “minimum” only when identity, model, time, period, and duplicate uncertainty allow a true lower bound. Missing history alone does not justify `N+` on an arbitrary reconstructed number. Otherwise use an explicit reconstruction/estimate label and keep unresolved evidence separate. See `docs/metering-contract.md`.
- Fake Monday/local reset anchors are allowed only for historical reconstruction estimates, never as authoritative quota reset times.
- Taskbar and floating widget must never show a reconstructed value in a form that looks like authoritative remaining quota.
- Lightweight server-status refresh must not require a full conversation-history scan.
- Count confidence must include confidence in the quota-period boundary. Untouched default Monday 00:00 local is not a confirmed reset.
- Last sync means completion time, not scan start/reference time.
- Coverage must describe concrete collection state, not imply a probabilistic accuracy percentage.
- The same conversation encountered through multiple indexes in one sync must not be counted as multiple unique failures.
- The same conversation should not be unnecessarily body-fetched twice in the same sync.
- Unknown denominators must use human-readable wording rather than a bare "Unknown".
- Estimated reset anchors must never visually resemble confirmed server reset times.
- NotifyIcon.Text must always satisfy the platform length limit.
- User-visible localization must never make tray tooltip assignment capable of crashing startup.
- Last successful/partial completed sync time must survive application restart.
- Persisted last-sync state must not be confused with an in-progress or failed sync.

## Privacy and security

- Never extract Chrome/Edge/Whale cookie databases.
- Never persist access tokens, session tokens, Authorization headers, or cookies.
- Access tokens may be cached only in process memory and must never be persisted or logged.
- Never store prompt or assistant response bodies.
- Never write prompt/response bodies or secrets into logs.
- No external telemetry or analytics.

## Authentication and origin

- Interactive OAuth navigation must never be interrupted by background probes.
- External authentication origins such as Google, Microsoft, Apple, and auth.openai.com may be visited only during an explicit interactive sign-in.
- Embedded WebView OAuth must not be presented as a supported authentication path for Google, Microsoft, Apple, or other providers that disallow embedded user agents.
- Never spoof a user agent or bypass an identity provider's embedded-browser restrictions.
- Social-login support must use the user's normal browser session through an explicit browser companion integration.
- WebView2 may remain only as an optional fallback for authentication methods that actually work in WebView2.
- ChatGPT backend fetches must execute only from the exact validated ChatGPT application origin, never from substring-matched or arbitrary origins.
- Backend execution origin and requested target URI must both be validated.
- Backend fetches require HTTPS and the exact expected origin.
- Absolute external URLs, scheme-relative URLs, non-default ports, backslashes, control characters, and path traversal must be rejected before credentials are attached.
- A failed session refresh must return the refresh failure, not mask it as the original 401.
- Do not fetch `/api/auth/session` before every backend request.
- Do not use host substring matching for origin validation.
- Login and session-probe operations must be single-flight and cancellable.

## Consent and disclosure

- Start with Windows and automatic history synchronization require explicit user opt-in.
- ChatGPT internal endpoints are unofficial and may change without notice.
- Any retained legacy history-access UI must disclose its unsupported status; active CodexMeter has no history access.
- Onboarding must never display a quota count as successfully loaded when sync failed.

## Transport

- Transport schema failures must never be reported as network offline errors.
- Chrome Native Messaging through runtime.connectNative is a long-lived, full-duplex channel. Never implement it as alternating request/reply I/O.
- Application-initiated messages must reach the extension without requiring a preceding extension message.
- Every bridge request must have a bounded timeout and must complete when the connection closes.
- Browser companion authentication is owned by the extension. ChatGPT access tokens may exist only in chatgpt.com page-local memory and must never cross Native Messaging, named pipes, logs, settings, SQLite, or exports.
- Page-context modules must not be reinitialized for every operation when doing so destroys authentication state.
- ChatGPT access tokens remain page-local only and must never cross the extension/native boundary.
- ProMeter must work with the user's normal browser networking configuration, including a browser VPN/proxy, when chatgpt.com itself works in that browser.
- Do not instruct users to disable VPN as a product requirement.
- Browser Companion ChatGPT requests should execute in the authenticated chatgpt.com page context when extension service-worker fetches do not share equivalent site/session context.
- ChatGPT credentials, cookies, access tokens and Cloudflare/session material must never cross into Native Messaging or ProMeter.
- Page-context execution must remain operation-allowlisted and metadata-only.
- The browser bridge is read-only except for narrowly approved requests that are indispensable to usage reconstruction.
- Arbitrary HTTP methods and arbitrary /backend-api paths are forbidden.
- Browser responses must be projected through endpoint-specific metadata allowlists before leaving the browser. Recursive denylist deletion is not an adequate privacy boundary.
- Named-pipe writes must be serialized and pipe access must be restricted to the current Windows user.
- Native-host registration must fail closed when the companion executable or extension ID is invalid.
- An encoded or canonicalized path must never escape an approved route.
- Browser companion setup must be completable from onboarding without first performing a failed sync.
- Transport migrations must never silently change an existing user's selected transport.
- Native-host caller-origin validation must use the actual argument shape received by the executable. Top-level C# `args` does not include the executable path.
- Tests for native-host invocation must reproduce Chrome's real Windows argument order.
- A bridge response operation must exactly match the pending request operation.
- Native-side projected-shape validation must be an endpoint-specific structural allowlist, not only a search for known forbidden keys.
- User-created project names and titles are not required for usage counting and must not cross the browser bridge.
- Payload limits must be calculated in UTF-8 bytes on both JavaScript and .NET.
- Native Messaging per-message size limits must not be bypassed by simply raising a constant.
- Large SAFE projected conversation metadata must use bounded chunked transport.
- Chunking applies only AFTER privacy projection.
- Raw ChatGPT conversation responses must never be chunked or forwarded.
- PayloadTooLarge is a transport-size condition, not SchemaMismatch.
- Conversation endpoint timeout and whole-conversation timeout are separate concepts.
- Alternate endpoint failure must not automatically imply global auth failure when live evidence proves that alternate endpoint is unsupported while the authenticated account/index path works.
- Page bridge schema/behavior changes require PAGE_BRIDGE_VERSION increment.
- Do not claim companion connectivity from pump tests that bypass the production native-host entrypoint.

## Diagnostic safety

- Diagnostic tests must never silently change the user's selected transport.
- WebView2 compatibility testing must use an isolated diagnostic flow.
- A failed WebView2 test must leave Browser Companion and existing settings untouched.
- Never delete or overwrite existing reconstructed usage data during a transport test.
- Interactive OAuth may require user input and must never be automated by spoofing, credential injection, user-agent spoofing, or identity-provider bypass.
- Lightweight WebView2 diagnostics must not perform a full history scan.
- A compatibility test must distinguish sign-in failure from API incompatibility.
- The existing Korean/English localization system must be reused. Do not create a second localization system.
- No WebView2 initialization/navigation operation may wait indefinitely.
- Every diagnostic WebView2 stage must support cancellation and a bounded timeout.
- A WebView2 control that needs an HWND/visual host must not depend on a permanently hidden/unrealized WPF Window for first initialization.
- WebView2 diagnostic progress/failure stages must be logged before the final result.
- Closing Settings or cancelling a diagnostic must release the diagnostic-running state.
- Browser Companion must remain untouched by WebView2 diagnostic failures.
- No WebView2 network request may wait indefinitely after initialization succeeds.
- WebView2 fetch execution must have a bounded request timeout.
- NavigationCompleted with IsSuccess=false is a navigation failure, not successful completion.
- Cancellation and timeout must return control to the UI even if WebView2 internally cannot synchronously abort every underlying operation.
- Timeout/error diagnostics must distinguish initialization, navigation and API request stages.
- Diagnostic failures must never alter Browser Companion state, selected transport, production usage data, watermarks, AutoSync or StartWithWindows.
- Every user-visible sync-error notification must first produce a corresponding safe diagnostic log entry.
- Offline and SignedOut sync failures must be logged without exception or network/session text.
- Duplicate desktop sync-error notifications for the same failure must be suppressed; logs must not be suppressed.
- Opening the Flyout must not immediately retry a just-failed automatic ChatGPT sync.
- Startup/UI/WebView2 regression paths require tests.
- Commit, push, remote SHA verification, and pushed CI verification remain mandatory.
- PAGE_BRIDGE_VERSION changes only if extension page-bridge JavaScript is modified.
- Browser/ChatGPT reachability and Browser Companion transport health are distinct.
- A disconnected Native Messaging bridge must never be labeled Network Offline.
- Known local bridge failures require explicit diagnostic categories.
- An opted-in Browser Companion should recover automatically from transient native-host disconnects without requiring the user to reopen the extension popup.
- Automatic reconnect must use bounded exponential backoff and must not busy-loop.
- Sync should tolerate a short transient companion disconnect before declaring failure.
- Extension reconnect changes require extension regression tests.
- If PAGE_FILES/page-bridge behavior is changed, increment PAGE_BRIDGE_VERSION. Merely changing background/native reconnect logic does not require a page bridge version bump.
- PAGE_BRIDGE_VERSION must be incremented whenever page bridge projection/schema behavior changes.
- Changes to extension projection require extension regression tests and the final report must explicitly tell the user to Reload the unpacked Edge extension.

## Architecture

- All ChatGPT internal API paths stay behind the provider/transport abstraction. UI must not hard-code backend endpoints.
- Preserve raw model slugs. Do not guess unknown future slugs into an existing model.
- Database reconciliation must preserve OfficialExport evidence unless explicitly superseded by equivalent verified evidence.
- A successful HTTP conversation response with zero parsed assistant usage must be diagnostically distinguishable from an actually empty conversation.
- Browser projection must support both nested mapping-node message shapes and direct paginated message shapes.
- Projection must canonicalize both server shapes into one metadata-only internal shape before crossing Native Messaging.
- UI callbacks originating from pipe/background threads must marshal to the WPF Dispatcher before touching controls.
- Dark-theme flyout values must never rely on the platform default foreground.
- Dark and light theme controls must never rely on Windows/WPF default foreground/background colors.
- Every visible TextBlock, TextBox, ComboBox, TabControl, DataGrid and context UI must be readable in Dark, Light and System themes.
- Stateful controls must make their current state obvious without hover or focus.
- Checked/selected state must never depend on Windows default theme chrome.
- Dark and light themes must both provide explicit visible selected, unselected, hover, pressed, focused, and disabled states.
- Checkbox/radio selection indicators must have sufficient visual contrast.
- Disabled checked controls must still visibly communicate that they are checked.
- Custom dark-theme controls must not rely on platform default ControlTemplates when those templates can ignore or conflict with app theme resources.
- UI-state regressions require tests.
- Commit, push, remote SHA verification, and CI verification remain mandatory.
- An incomplete reconstruction must not present "0 / quota" as though zero were a trustworthy usage measurement.
- User-visible UI strings must go through localization resources.
- Korean and English are supported UI languages.
- Model names, raw model slugs and product names are not translated.
- If Windows UI culture is Korean on a new install, default UI language to Korean; otherwise English.
- Codex quota data must come from the installed Codex App Server protocol.
- Do not read, copy, parse, monitor, or persist Codex auth.json, OAuth tokens, access tokens, cookies, Authorization headers, prompts, responses, projects, rollouts, or conversation files.
- Do not invoke a Codex model turn merely to measure quota.
- Rate-limit windows must be identified by `windowDurationMins`, not by assuming `primary` always means five hours or `secondary` always means weekly.
- A missing optional rate-limit window is not a provider failure.
- Never convert an unavailable percentage into zero.
- Preserve the last valid Codex snapshot on transient failure and mark it stale.
- Every spawned Codex App Server process must have bounded startup, request, shutdown, and cancellation behavior.
- Child processes must not be leaked after refresh, cancellation, app exit, or protocol failure.
- The taskbar status strip must not inject code or DLLs into explorer.exe.
- The taskbar status strip must not replace, subclass, or parent itself into an undocumented Explorer taskbar window.
- The taskbar status strip must never cover the clock or notification icons.
- If there is insufficient horizontal space, automatically use a more compact layout or place the strip immediately above the taskbar.
- Taskbar, DPI, display, Explorer restart, auto-hide and fullscreen changes must not leave the strip stranded or permanently visible in the wrong location.
- User-visible status must distinguish fresh, stale, unavailable, refreshing and failed data.
- A click intended to inspect status must not accidentally begin a long sync.
- All new UI strings must use the existing Korean/English localization system.
- Stateful controls and buttons must remain visible in dark, light and system themes.
- Manual refresh must provide unmistakable visible progress feedback.
- A static color change alone is not sufficient progress feedback.
- Combined manual refresh must be single-flight across every UI entry point.
- A duplicate refresh request must never clear another invocation's busy state.
- Only the invocation that owns the active refresh may set or clear the combined
  manual-refresh state.
- Refresh controls must remain disabled until the real shared refresh completes.
- Overlapping refresh behavior requires deterministic concurrency tests.
- Codex fixtures must match the official generated app-server protocol shape.
- Root response metadata and selected rate-limit bucket metadata must not be conflated.
- SystemEvents callbacks must marshal to the owning WPF Dispatcher before touching windows, controls, DispatcherTimer or presentation state.
- Taskbar auto-hide state must affect overlay visibility.
- Commit, push, remote SHA verification and pushed CI verification remain mandatory.

## Testing and completion

- Every correctness bug gets a regression test. Preserve existing tests.
- Malformed API fixtures must be synthetic and contain no real user content.
- Live compatibility tests must cover the actual response-shape families observed from ChatGPT, using synthetic fixtures without storing real content.
- Before completion run: `dotnet restore`, Release build, Release tests (`--no-build`), win-x64 self-contained single-file publish.
- Do not report PASS without actual command output.
- Keep the GitHub Actions Windows workflow green.
- Never claim live ChatGPT compatibility that was not actually validated.
- A task is not complete after a local commit. The agent must push the final commit to the configured origin unless the user explicitly says not to.
- Never force-push.
- After pushing, verify that the remote branch SHA exactly matches local HEAD.
- Do not report "complete", "pushed", or provide a commit for review until the remote SHA has been verified.
- If push authentication or network access fails, report the exact failure and clearly state that the commit remains local.
- A GitHub Actions run triggered by the pushed SHA should be reported as pending, passed, or failed when the GitHub CLI is available.
- Startup/UI formatting regressions require tests.
- Commit, push, remote SHA verification, and CI verification remain mandatory.

## Working style

- Inspect the existing implementation before changing architecture. Prefer small targeted changes.
- Do not leave TODO/mock implementations for required behavior.
- Perform a final diff review before committing.
- Report changed files, tests added, actual build/test/publish results, and remaining live-account verification items.
