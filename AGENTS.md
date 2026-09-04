# ProMeter agent instructions

Repository folder: `prometer`. Product name: **ProMeter**. Windows-only lightweight tray app. Stack: .NET 8, WPF, WebView2, Microsoft.Data.Sqlite.

## Purpose

Reconstruct ChatGPT Pro usage from **account-side conversation history**, including multi-device use (Windows PCs and mobile ChatGPT). Local browser request interception must never be the primary source of truth. Default UI is Tray Only; the floating widget is optional.

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
- Count confidence must include confidence in the quota-period boundary. Untouched default Monday 00:00 local is not a confirmed reset.

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
- The application must disclose that programmatic history access is unsupported and may carry account/terms risk.
- Onboarding must never display a quota count as successfully loaded when sync failed.

## Transport

- Transport schema failures must never be reported as network offline errors.
- Chrome Native Messaging through runtime.connectNative is a long-lived, full-duplex channel. Never implement it as alternating request/reply I/O.
- Application-initiated messages must reach the extension without requiring a preceding extension message.
- Every bridge request must have a bounded timeout and must complete when the connection closes.
- Browser companion authentication is owned by the extension. ChatGPT access tokens may exist only in extension service-worker memory and must never cross Native Messaging, named pipes, logs, settings, SQLite, or exports.
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
- Do not claim companion connectivity from pump tests that bypass the production native-host entrypoint.

## Architecture

- All ChatGPT internal API paths stay behind the provider/transport abstraction. UI must not hard-code backend endpoints.
- Preserve raw model slugs. Do not guess unknown future slugs into an existing model.
- Database reconciliation must preserve OfficialExport evidence unless explicitly superseded by equivalent verified evidence.

## Testing and completion

- Every correctness bug gets a regression test. Preserve existing tests.
- Malformed API fixtures must be synthetic and contain no real user content.
- Before completion run: `dotnet restore`, Release build, Release tests (`--no-build`), win-x64 self-contained single-file publish.
- Do not report PASS without actual command output.
- Keep the GitHub Actions Windows workflow green.
- Never claim live ChatGPT compatibility that was not actually validated.

## Working style

- Inspect the existing implementation before changing architecture. Prefer small targeted changes.
- Do not leave TODO/mock implementations for required behavior.
- Perform a final diff review before committing.
- Report changed files, tests added, actual build/test/publish results, and remaining live-account verification items.
