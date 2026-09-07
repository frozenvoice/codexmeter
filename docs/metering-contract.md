# ProMeter metering contract

This document defines reconstruction semantics. It is not a live ChatGPT API specification. Public plan limits cited here come from [GPT-5.6 in ChatGPT](https://help.openai.com/en/articles/20001354-gpt-56-in-chatgpt) and are not account-specific used/remaining observations.

## 1. Observed request vs fragment vs server debit

- An **assistant fragment** is one mapping/message node (analysis, tool, visible final).
- An **observed request** is one generation: strongly linked fragments of the same assistant turn.
- A **server debit** is a billed/quota deduction. History reconstruction never proves a debit unless the server supplies an exact used/remaining counter for that allowance and period.

Fragments of one generation stay one request across long durations, tool turns, hidden analysis, pagination, and later fetches.

Do not merge across:

- an intervening user request
- conflicting request IDs
- a demonstrated new generation
- independent regeneration branches

Sibling visible finals that share a user parent are not automatically one generation.

Identity is scoped to conversation (and account/workspace when those IDs exist). Opaque `request_id` values are not assumed globally unique. When an ID-less observation later gains a `request_id`, merge into the existing request; do not count both.

Genuinely unresolved identity is an **unresolved candidate**, not a fabricated extra confirmed request. Do not inspect prompt or assistant text, and do not hash content, to deduplicate.

## 2. Model and time provenance

Keep **requested model** and **observed response model** separate. Aggregate evidence across the resolved request group. An empty selected final must not erase explicit analysis/response metadata.

- A selected/requested Pro model alone does not prove Pro served the turn.
- Conflicting actual response identities stay ambiguous. Do not pick the interpretation that increases Pro usage.
- Catalog-derived mappings persist across `Resolve` calls and process restart. Do not guess unknown future slugs into Pro. Preserve raw slugs.

Timestamp provenance is required:

1. Times demonstrably attached to this generation (assistant fragment times).
2. The user-request timestamp of **this invocation** only. A regeneration must not inherit an old shared user parent's time as if it were new.
3. Unknown, if neither is available.

Never substitute conversation creation time, sync time, or `DateTimeOffset.UtcNow` for missing request time. Processing/observation timestamps use an injected clock.

If a request's known interval crosses a reset, the event is **period-ambiguous**: it is not confidently counted on either side. Identity confidence, model confidence, time confidence, and period confidence are separate. Do not claim OpenAI debit timing without server evidence.

## 3. Evidence retention and canonical requests

Retain **observations** (what was seen) separately from **canonical countable requests** (what is displayed after identity merge).

- A later narrower branch or page must not erase prior evidence by absence.
- Replaying observations, changing index source, response order, page size, or restarting must not increment the canonical count.
- Normal / archived / project indexes and official exports reconcile by matching identity, not by counting each source separately.
- Later `request_id` / model / time enrichment updates or merges one request atomically. Do not leave heuristic and explicit-ID rows both counted.
- A proven duplicate correction may reduce a derived count. Keep the evidence and a safe correction reason. Derived totals are not numerically monotonic.
- OfficialExport evidence is preserved.
- Null or partial data must not overwrite stronger known evidence.
- Evidence, aliases, derived rows, and applicable watermarks update in one transaction. Crash or cancellation must not leave a half-merge.

Completeness is scoped:

| Scope | Meaning |
| --- | --- |
| Readable current branch | `current_node` parent chain can be walked |
| Fetched-page exhaustion | pagination/cursors were followed until no previous page or a bound stopped the walk |
| Returned-graph validity | referenced parents/children that should be present are present |
| Requested-history coverage | the scan actually covered the history window being claimed |

A readable current chain is not complete account or branch history. Honor `has_previous_page`, cursors, and missing graph references. Budget exhaustion or missing older pages is incomplete coverage, not successful-empty history. Partial metadata may add safe evidence without deleting anything and must not advance a successful full-scope watermark.

## 4. Allowances, periods, and exact counters

A reset timestamp is not a full quota window. Carry allowance identity, plan/model scope, window kind or duration, observed-at time, reset/period boundary, source, and confidence.

Public policy (Help Center, not an account reading):

- **Pro $100**: GPT-6 Pro and GPT-5.6 Sol Pro share one **weekly** 50-message allowance.
- **Pro $200**: GPT-6 Pro **weekly**, Sol Pro **daily**, and a combined **daily** allowance are separate.

Within the **same** allowance, fresh applicable server evidence precedes a retained historical anchor. A fresh daily or unrelated-model reset must not replace a shared weekly anchor. Retained timestamps must not be presented as freshly observed future resets.

Quota-cycle reconstruction requires an applicable allowance/period and stays estimated. Never convert a daily or unknown reset into a weekly one.

With no applicable server/retained or explicitly configured reset anchor, show rolling **last 7 days**
observed-request statistics, using (now - 7 days, now]. This is a historical window, not a quota-cycle
estimate: no guessed Monday reset, next-reset date, exact remaining count, or mathematical lower bound.
Use the same window for history scanning so crossing Monday does not silently exclude weekend evidence.

Legacy unscoped `LastConfirmedResetAt` is retained. Attach scope only when existing metadata proves it; otherwise using it is uncertain. Do not erase a valid retained Pro100 weekly anchor without cause.

Exact used/remaining requires a matched allowance, a still-valid observed period, coherent nonnegative fields, and an explicit as-of/freshness policy. When the reset has passed, invalidate the old exact count. Do not carry 50/50 into the new period, invent zero, or mix an old server counter with newer local events into an exact total. Missing server counts do not erase the reconstructed ledger.

Sol **today** / **this week** are local-calendar analytics in the configured timezone and documented week start. They are not the Pro allowance period. If a metric is the current Pro cycle, label it as the cycle, not “this week”. Daily charts use the same local timezone.

## 5. Display

One presentation policy for Flyout, tray, main view, taskbar, widget, charts, and notifications:

- reconstructed distinct **observed requests** for the current Pro cycle
- unresolved identity/model/time/period evidence as a separate pending count
- exact remaining only from a valid server counter
- actual server restriction observation, separately from counts

Compact reconstructed form: cycle aggregate `N` with an estimate label; unresolved `M` omitted when zero. Use `N+` / “minimum” only when counted identity is stable and missing history can only undercount. That lower bound is observed requests, not proven billed quota.

Never derive `used / planLimit`, remaining, or a percentage from reconstructed usage plus a configured or public plan limit.

## 6. Migration and snapshots

Reconstruction semantics are versioned. Successfully scanned conversations with an old version must be revalidated, not only failed ones. Revalidation is bounded, resumable, rate-limited, and must not disable incremental sync.

Legacy rows lack some provenance; do not invent it. Do not count legacy rows and rebuilt replacements together. Migration is transactional, restart-safe, idempotent, and rollbackable via SQLite backup.

Private metering snapshots (sync completion, restriction/reset transitions, reconstruction corrections) store aggregate fields only: versions, capture time, allowance/period source, observation age, displayed reconstruction, unresolved categories, coverage, and separately named fetched/parsed/new/merged/current-period totals. No titles, URLs, prompts, assistant text, credentials, or raw conversation JSON.
