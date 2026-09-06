# ProMeter — Claude / agent notes

This repository is **ProMeter**, a Windows tray app that reconstructs ChatGPT Pro usage from account-side conversation history.

Before changing metering, reconstruction, quota periods, sync watermarks, or usage presentation, read:

1. `AGENTS.md` — product correctness, privacy, transport, and test/delivery rules
2. `docs/metering-contract.md` — request identity, evidence, allowances, and display semantics

Do not duplicate `AGENTS.md` here. Cursor model selection does not automatically load this file; follow the `.cursor/rules` pointer as well.

Privacy, authentication, and Native Messaging rules in `AGENTS.md` are unchanged by metering work.
