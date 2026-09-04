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
- Never store prompt or assistant response bodies.
- Never write prompt/response bodies or secrets into logs.
- No external telemetry or analytics.

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
