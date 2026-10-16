# Garbati.IdentityApi

GARBATI's identity, KYC and mandate service. It decides whether a customer is authorized to act on a ledger account and proves that decision to [LedgerApi](https://github.com/AbubakarImam/LedgerApi) with a signed, short-lived authorization grant — it never moves money or stores a balance itself.

**No code exists yet.** This repository currently holds only the design: [`docs/DESIGN.md`](docs/DESIGN.md) is the source of truth for every decision.

## Where this fits

See [`garbati-platform/ARCHITECTURE.md`](https://github.com/AbubakarImam/garbati-platform/blob/main/ARCHITECTURE.md) for the platform-wide map. In short: **Channel → Orchestrator → IdentityApi → Orchestrator → LedgerApi**. IdentityApi answers "is this customer allowed to do this?"; LedgerApi is the only thing that actually moves money.

## Status

Design draft v1 — several items are marked "Proposed" rather than confirmed; see `docs/DESIGN.md` Section 8 for the decisions log.
