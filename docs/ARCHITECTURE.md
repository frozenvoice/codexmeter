# ProMeter architecture

ProMeter reconstructs ChatGPT Pro usage from **account conversation history**, not from local request interception. History is the reconstruction input. Only a matching server quota counter is authoritative. That is what allows company PC, home PC, and mobile usage to share one meter.

```text
Browser companion (recommended)
  Chrome/Edge tab
    → MV3 extension (operation allowlist, SW-memory auth, endpoint projection)
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
- `extension/` — Manifest V3 companion. `https://chatgpt.com/*` only. No `cookies` permission.

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
- Responses are projected through endpoint-specific metadata allowlists before leaving the browser. Access tokens stay in service-worker memory only.
- Cookie databases are never read. Cookies and access tokens are never sent to the Windows app.

WebView2 remains an optional fallback for authentication methods that actually work there. Its profile lives under `%LOCALAPPDATA%\ProMeter\webview`. Access tokens stay in process memory only.

Backend fetches validate both the current HTTPS ChatGPT origin and the requested relative target before credentials or JavaScript are attached.

## Deduping

One `request_id` is one usage event, even when hidden reasoning, tool calls, and the final assistant message share that id. If `request_id` is missing, ProMeter falls back to conversation + message id.

All mapping nodes are scanned, so regenerate branches are counted when the server returns them.

## Privacy

Usage events store model, timestamps, request/message ids, and source metadata only. Prompt and response text are discarded.
