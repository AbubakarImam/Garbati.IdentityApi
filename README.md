# Garbati.IdentityApi

GARBATI's identity, KYC and mandate service. It decides whether a customer is authorized to act on a ledger account and proves that decision to [LedgerApi](https://github.com/AbubakarImam/LedgerApi) with a signed, short-lived authorization grant — it never moves money or stores a balance itself.

**No code exists yet.** This repository currently holds only the design: [`docs/DESIGN.md`](docs/DESIGN.md) is the source of truth for every decision — what IdentityApi does, its data model, the token design, and (new in v2) how joint accounts with configurable multi-holder approval work. Read it before writing any code against this service.

## Where this fits

See [`garbati-platform/ARCHITECTURE.md`](https://github.com/AbubakarImam/garbati-platform/blob/main/ARCHITECTURE.md) for the platform-wide map of which service owns which truth. In short: **Channel → Orchestrator → IdentityApi → Orchestrator → LedgerApi**. IdentityApi answers "is this customer allowed to do this?"; LedgerApi is the only thing that actually moves money.

## Status

Design v2, ratified 16 October 2026 (`garbati-platform` decisions D-6 and D-9). One item is explicitly **not** designed yet and should not be inferred from the rest of the document: reversal authority, reason codes, and reconciliation (tracked as `garbati-platform` T-015).

Building this service is the next step (`garbati-platform` task board).
