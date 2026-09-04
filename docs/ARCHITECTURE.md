# ProMeter architecture

ProMeter reconstructs ChatGPT Pro usage from **account conversation history**, not from local request interception. That is what allows company PC, home PC, and mobile usage to share one meter.

```text
WebView2 session  →  ChatGptProvider  →  SyncEngine  →  SQLite
                                            ↓
                                      QuotaEngine
                                            ↓
                                 Tray / Flyout / Stats
```

## Layers

- `ProMeter.Core` — models, ChatGPT provider abstraction, parsers, SQLite, quota, import/export
- `ProMeter` — WPF tray app, WebView2 login, notifications, startup

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

These are unofficial internal endpoints the ChatGPT website itself uses. They can change.

## Authentication

A dedicated WebView2 profile under `%LOCALAPPDATA%\ProMeter\webview` holds the ChatGPT session. Access tokens stay inside the page and are never written to SQLite, logs, or exports.

## Deduping

One `request_id` is one usage event, even when hidden reasoning, tool calls, and the final assistant message share that id. If `request_id` is missing, ProMeter falls back to conversation + message id.

All mapping nodes are scanned, so regenerate branches are counted when the server returns them.

## Privacy

Usage events store model, timestamps, request/message ids, and source metadata only. Prompt and response text are discarded.
