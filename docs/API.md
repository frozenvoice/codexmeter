# ChatGPT provider notes

The retained legacy provider does not invent unofficial fields. It reads JSON defensively and treats unknown shapes as `Provider schema mismatch` instead of crashing. This document covers retired ChatGPT collection, not the active Codex App Server integration.

## Confirmed model slugs

| Raw slug | Display | Family |
| --- | --- | --- |
| `gpt-5-6-pro` | GPT-5.6 Sol Pro | GPT Pro |
| `gpt-5-6-thinking` | GPT-5.6 Sol | Sol reasoning |
| `gpt-5-6` / `gpt-5-6-instant` | GPT-5.6 Sol | Instant (hidden in default UI) |

GPT-6 Pro internal slugs are **not** hard-coded. When a catalog title or an observed `*-pro` slug appears, it is recorded in SQLite `observed_models` and classified from those signals.

## Reasoning fields inspected

- `metadata.reasoning_effort`
- `metadata.thinking_effort`
- `metadata.effort`
- nested `model_experience`

Normalized values: `none`, `low`, `medium`, `high`, `extra_high`, `unknown`. Korean UI labels such as `매우 높음` map to Extra High.

## Reference projects (ideas only)

MIT-licensed exporters and counters were reviewed for endpoint and parsing ideas, not copied as a product:

- Bsnblanc/chatgpt-model-usage-counter — request vs response model, one submission / one event
- joningi/chatgpt-export — pagination, backoff, full mapping tree vs current branch
- pionxzh/chatgpt-exporter and later 2026 exporters — archived + project/gizmo index
