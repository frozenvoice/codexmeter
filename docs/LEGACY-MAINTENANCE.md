# Retained legacy maintenance

Read this only when modifying retained ChatGPT history/reconstruction, browser transport,
WebView, companion-host code or their regression fixtures. These paths are retired and do
not authorize a live collector, extension installation/reload, browser login or history scan.
The current Codex/Claude runtime follows [AGENTS.md](../AGENTS.md).

This consolidates the historical rules formerly mixed into the root instructions. Read
the relevant [metering contract](metering-contract.md) for reconstruction/evidence work,
or the historical section of [architecture](ARCHITECTURE.md) for legacy transport work.
[API notes](API.md) record prior findings; they are not a supported live API contract.

## Counts, evidence and synchronization

- Never turn an unknown/malformed response into a successful empty scan or silently undercount.
  Surface schema mismatch; distinguish a response with no parsed assistant usage from a truly empty conversation.
- Incomplete pagination reduces coverage. Partial/failed scans and missing `update_time` must not advance
  a trusted successful watermark. Last sync means completion time, not the scan's reference/start time.
- Global 401/403, 429, offline or fatal transport/5xx failures abort the scan. Do not record every
  remaining conversation as independently failed after that failure. Persist last-success/partial-completion
  state separately from in-progress and failed attempts, and retain it across restart.
- Do not body-fetch a conversation unnecessarily or count repeated index encounters as multiple failures.
  Preserve raw model slugs and keep ambiguous metadata diagnostic-only; do not infer unknown model families.
- History reconstruction is never an authoritative server debit or exact remaining quota. A local plan limit
  cannot make reconstructed usage exact. `N+` is allowed only when request identity, model, timing, duplicates
  and period uncertainty support a true lower bound; missing history alone does not.
- Keep count and reset confidence separate. Do not mix daily and seven-day periods or present an inferred
  Monday/local reset anchor as confirmed. Explicit server reset metadata does not establish exact usage.
- Do not infer quota exhaustion from a null block reason or an explicit relationship from correlated
  `blocked_features`. Preserve unresolved evidence rather than inventing a server relationship.
- Keep observed requests distinct from fragments and billed debits; normalize known direct and mapping-node
  shapes before canonicalization. Preserve OfficialExport and other stronger evidence during reconciliation.
- Every retained display must use the same reconstruction semantics. Never show incomplete data as `0 / quota`
  or label reconstructed requests as billed usage. Coverage describes collection state, not a made-up accuracy score.

## Authentication and request boundaries

- Never extract browser cookie databases or copy credentials. If maintaining the legacy companion, browser
  tokens remain only in the exact chatgpt.com page context; never forward them through Native Messaging,
  pipes, logs, settings, SQLite or exports. Do not retain bodies, project names/titles or unrelated metadata.
- Validate both the exact HTTPS origin and the requested endpoint. Reject substring/suffix lookalikes,
  user-controlled absolute or scheme-relative URLs, non-default ports, backslashes, control characters and traversal.
- Keep requests behind the provider/transport abstraction and allowlist operations/methods/routes.
  Project responses through endpoint-specific field allowlists; recursive denylist removal is not sufficient.
- A failed session refresh stays a refresh failure. Do not probe `/api/auth/session` before every request,
  interrupt OAuth with background probes, spoof user agents, inject credentials or bypass identity providers.
- WebView is not a workaround for providers that reject embedded sign-in. Legacy social authentication used
  the normal browser companion flow. Do not change the user's networking/VPN or selected transport for a test.
- Distinguish browser reachability, bridge disconnection, schema failure, session failure and network offline.
  A known local bridge failure is not evidence that ChatGPT is offline.

## Native Messaging and browser bridge

- The runtime port is a persistent full-duplex channel. Support app-initiated messages without a preceding
  extension request; bind each response to the exact requested operation and fail pending requests on disconnect.
- Bound startup, request, fetch, initialization and shutdown. Keep whole-conversation and individual-fetch
  deadlines separate. Cancellation must return control even if an embedded browser cannot synchronously abort.
- Serialize named-pipe writes and restrict access to the current Windows user. Native registration fails closed
  for invalid executable paths or extension IDs. Encoded/canonicalized paths cannot escape approved routes.
- Native caller-origin checks and tests use real C# argument positions: `args` excludes the executable path.
  Pump-only tests do not prove the production native-host entry point works.
- Do not destroy authentication state by reinitializing page modules for every operation. Preserve bounded
  reconnect/backoff for opted-in legacy fixtures and tolerate brief disconnects without busy polling.
- Measure payload limits as UTF-8 bytes on JavaScript and .NET sides. Oversize is not schema mismatch.
  Chunk only projected metadata; never bypass native limits by raising them or forwarding raw conversations.
- Do not conflate alternate-route failure with global auth failure when validated evidence shows the endpoint
  is merely unsupported. Preserve unrelated settings and explicit transport choice during any migration.
- The extension source is now retained in Git history only. If the user explicitly requests work on it,
  changes to page projection/schema require a `PAGE_BRIDGE_VERSION` increment and matching regression checks;
  background/native reconnect alone does not. Mention reload only when actually delivering an extension change.

## Isolated diagnostics

- Use synthetic fixture data. Diagnostics cannot silently alter real transport, accounts, quotas, watermarks,
  AutoSync, startup preference, browser state or saved settings. Never delete historical data to make a test pass.
- A WebView diagnostic that requires an HWND must use a realized visual host, with bounded stages and
  cancellation. Navigation completion with `IsSuccess=false` is a failure, not a successful navigation.
- Surface the failing stage without secrets; preserve actual auth versus API-compatibility distinctions.
  Log safe categories before notifications, suppress duplicate desktop notices without losing diagnostic events,
  and avoid retry storms from merely opening the flyout after a failure.
- Closing settings/cancelling releases the diagnostic busy state. Failed diagnostics leave the previous
  companion/transport and saved data intact. Do not claim live compatibility from synthetic checks.

Use the root verification/delivery rules for the actual change. This document does not impose a full
history scan, live sign-in, extension setup or a separate full test/publish cycle on unrelated work.
