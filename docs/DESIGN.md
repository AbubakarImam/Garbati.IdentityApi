# Garbati.IdentityApi — Design Document (v2)

**Status:** Design v2 — ratified. All decisions below are Confirmed by the owner (Abubakar), most cross-checked against published standards before being confirmed (see citations throughout). No code exists yet; this document is what the first builder task implements.
**Last updated:** 16 October 2026
**Service name:** `IdentityApi` (confirmed, decision #18 below)
**Platform:** Garbati — see [`garbati-platform/ARCHITECTURE.md`](https://github.com/AbubakarImam/garbati-platform/blob/main/ARCHITECTURE.md) for the platform-wide map; this document never repeats that file, only this service's detail.
**Decision history:** `garbati-platform/TASKS.md` D-9 (this document's ratification) and D-6 (token-verification timing). Supporting research: `garbati-platform/research/identityapi-design-review.md` (token design vs. RFC 8725/9068/7009/OWASP) and `garbati-platform/research/joint-account-approval-patterns.md` (joint-account approval mechanics vs. maker-checker/multisig/enterprise-approval practice).

## Purpose and decision status

This is v2 of the original design draft (preserved in git history; see the first commit of this file). v1 recommended a single-holder mandate model and left several items as "Proposed" or genuinely unresolved. v2 closes every one of those gaps and adds a materially new capability — joint accounts with configurable multi-holder approval — that the owner decided to bring into v1 scope rather than defer. The decisions log (Section 9) is the full record of what changed and why; read it alongside this section if you want the history, not just the current state.

### Existing LedgerApi decisions carried forward

- **Decision #12:** LedgerApi does not store customer identity data such as email, BVN or NIN. That data has different access, retention and regulatory requirements and belongs outside the ledger.
- **Decision #34:** LedgerApi authorizes calling services, not customers. Customer-level authorization must happen before a trusted service asks LedgerApi to move money.
- **Decision #35:** Services authenticate to LedgerApi using API keys and scopes.

### Confirmed architecture decisions

1. The platform is named **Garbati**.
2. The money-movement flow is **Channel → Orchestrator → IdentityApi → Orchestrator → LedgerApi**.
3. IdentityApi checks customer eligibility and mandate authority, then issues a **cryptographically signed, transaction-bound authorization grant**.
4. The Orchestrator may verify the grant before forwarding it, but LedgerApi must independently verify the grant and compare it with the actual transaction it receives — **once decision #47 is built** (see D-6 below: this check is deliberately deferred for the first demo).
5. LedgerApi must **not call IdentityApi directly** for each transaction. It verifies the grant locally using IdentityApi's trusted public key.
6. Authorization grants have a **30-second validity window** in v1.
7. The design accepts a limited race window in which a mandate may be revoked after a grant is issued but before it is used.
8. **(v2)** IdentityApi generates `CustomerId` itself; it never travels into LedgerApi. The Orchestrator carries it from account creation (IdentityApi) through to mandate attachment (IdentityApi), and LedgerApi never learns a customer exists (decision #12 stays intact). See decision #16.
9. **(v2)** Joint accounts are in v1 scope: an account can have more than one active mandate holder, with a configurable number of holders required to approve a debit. See Section 3.8 and decisions #28–#34.
10. **(v2)** Decision-token verification on the LedgerApi side (decision #47) is deliberately **not built yet** — the demo ships without it, documented as a known limitation, and built once OrchestratorApi exists to actually call it (`garbati-platform` D-6). IdentityApi still issues real signed grants from day one; only LedgerApi's verification step is deferred.

---

## 1. What is this system?

### 1.1 Purpose and callers

IdentityApi owns customer identity records, KYC status, and the mandates that connect customers to Garbati ledger accounts — including, as of v2, **more than one** mandate per account for joint ownership. It decides whether a customer is authorized to perform a particular operation on a specific account, under the applicable customer, KYC, mandate, and (for joint accounts) multi-holder approval rules.

IdentityApi does not hold ledger balances or post financial entries. A trusted Orchestrator coordinates requests between channels, IdentityApi and LedgerApi. LedgerApi remains authoritative for account state, balance rules, idempotency and the financial posting itself.

### 1.2 Scope for v1

| Capability | V1 decision | Notes |
|---|---|---|
| Customer record creation and maintenance | In scope | Customer can exist before being fully verified. |
| KYC status and current tier | In scope | Tier rules are configurable policy data, not hardcoded (decision #11). Exact Nigerian tier limits still need compliance confirmation — `garbati-platform/research/nigerian-regulatory-requirements.md`. |
| Customer-to-account mandate | In scope | **v2: multiple holders per account (joint accounts), not single-customer only.** See 2.2 and 3.8. |
| Configurable multi-holder approval (M-of-N) | In scope (v2) | Section 3.8. Simple unweighted count only; no weighted votes or named-required-approvers in v1 (decision #20). |
| Authorization check and signed grant issuance | In scope | Grant is specific to the transaction intent and valid for 30 seconds. Issued regardless of how many holders had to approve first. |
| Customer authentication, passwords and end-user sessions | Out of scope | Owned by the channel/application (decision #15). IdentityApi receives a trusted customer identity from the authenticated flow. |
| Biometric capture or biometric matching | Out of scope | A future provider or workflow may supply a verification result. |
| OCR or raw identity-document storage | Out of scope | External secured storage and references only (decision #12). |
| Credit scoring or financing eligibility | Out of scope | Separate product/domain responsibility. |
| Reversal authority, reason codes, reconciliation for unrecoverable external effects | **Not designed yet** | Deliberately deferred — `garbati-platform` T-015. Do not build this from inference; wait for that task. |
| Compliance suspension, mandate revocation and privileged review | In scope | Restricted to authorized internal services or operators and fully audited. |
| Full external KYC provider integration | Out of scope for initial build | Keep the model extensible for future integrations. |

### 1.3 Actors

- **Customer:** Acts through an approved channel; does not directly call the internal IdentityApi.
- **Channel:** Authenticates the end customer, owns login/sessions entirely (decision #15), and submits the transaction request.
- **Orchestrator:** Coordinates the flow; is the **only** component that calls LedgerApi for anything that changes ledger state, including account freezes (decision #17) — IdentityApi never calls LedgerApi to change state, only (narrowly) to read, per 2.4.
- **IdentityApi:** Checks KYC, customer status, and mandate/approval permissions; signs an authorization grant when the request is approved.
- **LedgerApi:** Verifies the grant (once #47 is built — see D-6), checks financial and account rules, and posts or rejects the transaction.
- **Operations/compliance actor:** Performs restricted KYC review, customer suspension/reactivation and mandate revocation where authorized.
- **Mandate holder (v2):** A customer with an active mandate on a joint account. May be the sole holder (ordinary account) or one of several (joint account).
- **External verification provider:** May later provide KYC results through a restricted integration.

---

## 2. Core entities

### 2.1 Customer

Use an internally generated `CustomerId` as the stable identifier (decision #16) — never BVN, NIN, or an account number.

Fields:

- `Id` / `CustomerId`
- `CustomerReference` (optional internal reference)
- `CustomerStatus`: `Active`, `Suspended`, `Deceased` or `Closed`
- `KycStatus`: `Pending`, `Verified`, `Rejected` or `Expired`
- `KycTier`: current tier identifier
- `JurisdictionCode`
- `CreatedAt`, `UpdatedAt`

Sensitive identity fields are separated from ordinary operational fields (`CustomerIdentityData`, 7.1), protected by encryption, least-privilege access and an explicit retention policy.

A customer **may exist with zero mandates**. Registration does not by itself grant permission to operate a ledger account. An unverified or otherwise ineligible customer must not receive an authorization grant for an operation that requires a higher KYC status/tier.

**v2 — status-change cascades (decision #14):**

| Status becomes | Effect | Reversible? |
|---|---|---|
| `Suspended` | No new authorization grants issued. Linked accounts are **not** automatically frozen — a separate, explicit freeze workflow (2.5) may be triggered if the suspension reason warrants it. | Yes — reactivation is possible after review. |
| `Closed` | **Automatic:** every mandate held by this customer is revoked, and every ledger account where this was the customer's *only* active mandate is frozen via the Orchestrator (2.5). A joint account where other holders remain active is **not** frozen — only this customer's own mandate is revoked; the account continues to operate under its remaining holders and current approval threshold. | No — `Closed` is terminal. |
| `Deceased` | **Automatic:** identical cascade to `Closed` (every mandate revoked; solely-held accounts frozen). A joint account with surviving holders is **not** automatically frozen — estate/succession handling for a deceased joint holder's share is an operations/legal process outside this service's v1 scope, flagged in Section 10. | No — terminal. |

This is a hard behavioral change from v1, which only specified `Suspended`'s effect. `Closed` and `Deceased` are terminal and irreversible by design — there is no scenario where access should silently resume, so cascading automatically removes the risk of a manual step being forgotten.

### 2.2 Mandate

A mandate links **one customer** to **one existing Garbati ledger account** and specifies the operations that customer is authorized to request on it. **v2: an account may have more than one active mandate — one per holder — to support joint accounts.**

`Mandates` fields:

- `Id`
- `CustomerId`
- `LedgerAccountId` (a cross-service identifier, not a cross-database foreign key)
- `Status` (`Active`, `Revoked`)
- `CreatedAt`, `CreatedBy`
- `RevokedAt`, `RevokedBy`, `RevocationReason`

`MandatePermissions` fields:

- `MandateId`
- `Operation` (`View`, `Debit`, `Credit` as applicable)

**v2 rule (decision #19, revising v1's single-mandate rule):** at most one **active** mandate per `(CustomerId, LedgerAccountId)` pair — enforced by a real database unique constraint (partial unique index `WHERE Status = 'Active'`), not application-level validation alone. Multiple *different* customers may each hold their own active mandate on the same account — that is what makes an account "joint." A mandate cannot become active until the referenced ledger account's existence has been confirmed.

A revoked mandate is retained for history; it is not deleted or silently reactivated. Restoring access creates a new mandate with a new identifier.

### 2.3 KYC verification and tier policy

Unchanged from v1, with the "configurable, not hardcoded" recommendation now **Confirmed** (decision #11): the customer's current tier is stored on the customer record; verification attempts/results are recorded separately; `KycTierPolicies` holds configurable jurisdiction/tier rules with effective dates, rather than limits spread through application code.

`KycVerifications` fields: `Id`, `CustomerId`, `VerificationType`, `Provider`, `ProviderReference`, `Status`, `ResultCode`, `VerifiedAt`, `ExpiresAt`, minimal audit metadata.

`KycTierPolicies` fields: `JurisdictionCode`, `TierCode`, `EffectiveFrom`, `EffectiveTo`, required verification evidence and eligible operations, applicable transaction rules/limits (where confirmed and assigned an authoritative enforcement point).

Start with a Nigeria-oriented policy profile, but the actual limits must be confirmed by compliance before production (`garbati-platform/research/nigerian-regulatory-requirements.md` already flags this as unresolved).

### 2.4 Account creation and mandate attachment

Unchanged mechanism from v1, with the `CustomerId` flow made explicit (decision #16 — this is exactly the question the owner raised and confirmed mid-design):

1. Register the customer in IdentityApi — `CustomerId` is generated here, internal to IdentityApi.
2. Complete required KYC checks and record the result.
3. **The Orchestrator** (not IdentityApi) calls LedgerApi to create the ledger account. LedgerApi generates its own `account_number`; it never receives or stores `CustomerId` (decision #12 stays intact).
4. The Orchestrator asks IdentityApi to attach a mandate, passing both `CustomerId` and the new `LedgerAccountId`. IdentityApi records this pairing in its own `Mandates` table — this is the **only** place the customer-to-account link exists.
5. **(v2, joint accounts)** For a joint account, step 4 repeats once per holder, each creating their own `Mandate` row against the same `LedgerAccountId`. The account's `MandateApprovalPolicy` (2.2a) is set at this point, before any holder can transact.

If account creation succeeds but mandate creation fails, the account remains without an active mandate. Retrying mandate creation must not create a duplicate ledger account. IdentityApi may validate an account reference through a narrowly scoped, **read-only** LedgerApi call during mandate setup; this is the one documented exception to "IdentityApi never calls LedgerApi," and it is read-only, never state-changing (decision #17 draws this line explicitly).

### 2.2a MandateApprovalPolicy (v2, new entity)

Every ledger account that IdentityApi knows about (via at least one mandate) has exactly one approval policy, created alongside its first mandate.

Fields:

- `LedgerAccountId` (unique)
- `RequiredApprovals` (`M`) — an integer ≥ 1
- `CreatedAt`, `LastChangedAt`, `LastChangedBy` (a reference to the consent record that approved the change, 3.8.4)

`RequiredApprovals = 1` is the ordinary single-holder case (any one of the account's — usually one — holders acts alone; this is also the default for an account that only ever has one holder). `RequiredApprovals > 1` only makes sense once the account has at least that many active mandates; IdentityApi must reject a `RequiredApprovals` value greater than the account's current active-holder count.

**v1 scope (decision #20):** `RequiredApprovals` is a simple, unweighted count over an unordered set of holders — "any M of N." No weighted votes, no naming of specific required individuals (e.g. "must include the primary holder"). This matches the standard default found across banking mandate types ("any one," "all," "any two"), bare multisig, and basic enterprise approval tiers (`research/joint-account-approval-patterns.md` Q2) — not the more complex variants those same domains also document.

### 2.5 Customer status and ledger account status

These are different controls and must not be conflated:

- `CustomerStatus` says whether the customer is permitted to operate through IdentityApi.
- `KycStatus`/`KycTier` says whether the required verification policy is satisfied.
- LedgerApi account status says whether the account itself can be used under ledger rules.

**v2 — freeze ownership (decision #17):** IdentityApi never calls LedgerApi to change account state. When IdentityApi determines an account must be frozen (customer suspended with a freeze-warranting reason, or closed/deceased per 2.1), it writes a durable `OutboxMessages` row (7.1) describing the freeze request. **The Orchestrator** polls/consumes this outbox, calls LedgerApi to actually freeze the account, and writes back an acknowledgement. IdentityApi tracks acknowledgement state and retries (via the same outbox mechanism) until it is confirmed — a freeze is never marked complete just because a message was queued. This keeps the Orchestrator as the single gate for every ledger-state-changing action (matching `ARCHITECTURE.md` P-1), with no new exception carved out for IdentityApi.

LedgerApi must still independently enforce its own account freeze/status rules regardless of what IdentityApi believes has happened.

---

## 3. Mandate rules and transaction authorization

### 3.1 Domain invariant

> A customer-originated operation must never be authorized unless the customer is eligible to operate, an active mandate permits the requested operation on the exact ledger account, and — for an account requiring more than one approval — enough of that account's holders have given a currently-valid approval for this exact request. If a valid decision cannot be established, no authorization grant is issued and the transaction must not proceed to LedgerApi.

Supporting invariants:

- The signed grant must refer to the exact transaction approved by IdentityApi.
- The received transaction must match all financially material fields in the grant.
- A grant must not authorize a different transaction, account, amount, currency or operation.
- A mandate cannot reference an unconfirmed customer or ledger account.
- Revocation and privileged status changes must be auditable and must not silently delete history.
- Failure to obtain an authorization decision must never be interpreted as approval (decision #10, fail-closed, confirmed — matches Saltzer & Schroeder's fail-safe-defaults principle).
- **(v2)** A holder's already-given approval toward a joint account's threshold becomes void, immediately, the moment that holder's mandate is suspended or revoked — it is never carried forward as if still valid (decision #24).

### 3.2 Confirmed transaction flow — single-holder (`RequiredApprovals = 1`)

```text
Channel
  -> Orchestrator
  -> IdentityApi
       - validate customer status and KYC eligibility
       - validate active mandate and operation permission
       - evaluate applicable authorization policy
       - issue a signed, transaction-bound grant if approved
  -> Orchestrator
       - optionally verify signature and transaction binding as defence in depth
       - forward the original transaction and signed grant
  -> LedgerApi
       - authenticate the Orchestrator as a permitted service
       - independently verify IdentityApi's signature and grant claims (once #47 is built — see D-6)
       - compare the received transaction with the approved transaction details
       - enforce ledger/account/balance/idempotency rules
       - post atomically or reject
```

**LedgerApi must not make a synchronous call to IdentityApi to validate each grant.** It verifies the grant locally using a trusted public key (`garbati-platform/research/identityapi-design-review.md` confirms this matches RFC 9068's rationale for local JWT validation over per-request introspection).

### 3.3 Signed transaction authorization grant

IdentityApi issues a cryptographically signed grant only after a successful authorization check — for a joint account, only once enough approvals (3.8) have been collected. Use a standard signed token format (JWS/JWT, asymmetric algorithm — ES256, matching decision #4's choice of an asymmetric signed grant; the algorithm must be pinned on the verifier side per RFC 8725, `research/identityapi-design-review.md` Q1). IdentityApi retains the private signing key; the Orchestrator and LedgerApi receive only the public verification key(s).

The grant binds, at minimum:

- Issuer (`iss`, `Garbati.IdentityApi`)
- Intended recipient (`aud`, `Garbati.LedgerApi`)
- Unique grant/decision identifier (`jti`)
- Customer identifier of the **requester** (`sub`) — for a joint account, the holder who initiated the request, not every approver
- Explicit type (`typ`: `"transaction-authorization"`) — defends against cross-JWT confusion (RFC 8725 §3.11)
- Operation type
- Debit/source account identifier
- Credit/destination account identifier, when applicable
- Exact amount and currency in a canonical representation (G29, matching LedgerApi's own precedent)
- Ledger request idempotency key or immutable transaction-intent identifier
- Mandate identifier(s) used for the decision — for a joint account, the requester's own mandate id; the fact that the threshold was met is IdentityApi's internal record (`PendingApprovalRequest`, 3.8), not repeated inside the grant itself
- Issued-at time (`iat`) and expiry (`exp`, 30 seconds)
- Grant/schema version
- Key id (`kid`) identifying which of IdentityApi's public keys verifies this grant (supports rotation, 4.3)

Do not include BVN, NIN, raw identity data or other unnecessary sensitive data. **Algorithm and `kid` pinning must be enforced in the verifier's configuration, not read from the token** (RFC 8725 §3.1 — the verifier decides the expected algorithm in advance; it never trusts the token to say what algorithm to use).

### 3.4 Verification responsibilities

**Orchestrator:** may verify the grant signature, issuer, expiry and transaction binding before forwarding. It must not modify the transaction after approval.

**LedgerApi:** is the final verifier at the point of money movement — **once decision #47 is built** (D-6: deferred for the first demo). When built, it independently checks: signature validity against a trusted key selected by `kid`; issuer, audience, type/version, issue time and expiry; that the signed operation, accounts, amount, currency and idempotency/intent identifier match the actual incoming request; that the grant has not been used to authorize a different transaction; and that the actual operation passes LedgerApi's own account-status, balance, idempotency and double-entry rules. On success, LedgerApi stores the grant/decision identifier with the transaction under a unique constraint.

**Two sources of truth:** IdentityApi is authoritative for customer identity, KYC, mandates and (v2) approval-threshold satisfaction; LedgerApi is authoritative for account/ledger state and whether money was actually posted. The Orchestrator owns neither truth.

### 3.5 Example grant (illustrative claims, not a token to copy into production)

```json
{
  "iss": "Garbati.IdentityApi",
  "aud": "Garbati.LedgerApi",
  "typ": "transaction-authorization",
  "jti": "unique-grant-id",
  "sub": "customer-id-of-requester",
  "kid": "identity-key-2026-10",
  "operation": "transfer",
  "debitAccountId": "account-A",
  "creditAccountId": "account-B",
  "amount": "50000.00",
  "currency": "NGN",
  "idempotencyKey": "payment-789",
  "mandateId": "mandate-456",
  "iat": 1792000000,
  "exp": 1792000030
}
```

### 3.6 Mandate revocation and the 30-second window

Unchanged from v1. Authorization grants expire 30 seconds after issuance. The design explicitly accepts a narrow race: a mandate may be revoked after IdentityApi issues a grant but before LedgerApi consumes it (`garbati-platform/research/identityapi-design-review.md` names this the standard RFC 7009 trade-off — a short-lived token substituting for instant revocation). New grant requests after revocation are denied. An urgent post-execution recovery can use LedgerApi's reversal process (T-015 — not yet designed).

### 3.7 Replay and retries

Unchanged from v1. A valid signature alone does not prevent replay; LedgerApi binds use of the grant to the exact request and enforces its existing idempotency rules. For a grant arriving after expiry, LedgerApi rejects it; the orchestrator/channel may request a fresh authorization.

### 3.8 Joint-account approval workflow (v2, new)

This section only applies when an account's `MandateApprovalPolicy.RequiredApprovals > 1`. For `RequiredApprovals = 1` (the ordinary case), nothing here changes 3.2–3.7: one holder's own mandate is enough.

#### 3.8.1 What changes

The single-holder flow is "check one mandate, issue a grant" in one synchronous call. The joint-account flow splits this into two phases:

1. **Collect approvals.** A holder requests a debit; IdentityApi opens a `PendingApprovalRequest` and counts the requester's own approval toward the threshold (decision #21 — the initiator **does** count as one of the M, unlike strict maker-checker; `research/joint-account-approval-patterns.md` Q1 flagged this as the one real choice between domains, and the owner chose the simpler multisig-style option). Other holders approve the same pending request through their own channel session.
2. **Issue the grant.** Once `M` distinct, currently-valid holder approvals exist for one `PendingApprovalRequest`, IdentityApi performs the ordinary single-holder authorization check (3.2) using the requester's own eligibility as the basis, and issues the signed grant exactly as before. Nothing about the grant itself (3.3) changes — it is still issued to, and verified by, exactly the same recipients.

#### 3.8.2 Entities

**`PendingApprovalRequest`:**
- `Id` (the request-specific identifier every approval is bound to — decision #25, this is the nonce-equivalent that stops an approval given for one request ever counting toward a different one)
- `LedgerAccountId`
- `RequestedBy` (`CustomerId`)
- Transaction intent: `Operation`, `DebitAccountId`, `CreditAccountId`, `Amount`, `Currency`, `IdempotencyKey` — the same canonical fields that will eventually appear in the grant (3.3), fixed at request-creation time so a later approval cannot be tricked into approving a changed transaction
- `RequiredApprovals` (copied from the account's policy at request-creation time, so a concurrent policy change does not retroactively change what this specific request needs)
- `Status`: `Pending`, `Approved`, `Expired`, `Cancelled`
- `CreatedAt`, `ExpiresAt` (`CreatedAt + 1 hour` — decision #23)

**`ApprovalRecord`:**
- `PendingApprovalRequestId`
- `CustomerId` (the approving holder — may be the same as `RequestedBy`, since the requester's own approval is recorded as the first `ApprovalRecord`)
- `ApprovedAt`
- `VoidedAt`, `VoidedReason` (nullable — set immediately if this holder's mandate is later suspended/revoked while the request is still `Pending`, decision #24)

Unique constraint on `(PendingApprovalRequestId, CustomerId)` — a holder cannot approve the same request twice, and this is also how the requester's own implicit approval is represented (one row, created at the same time as the request itself).

#### 3.8.3 Timeout and expiry (decision #23)

A `PendingApprovalRequest` expires **1 hour** after creation if it has not reached `RequiredApprovals` valid, non-voided `ApprovalRecord`s by then. On expiry: the request moves to `Expired` (an explicit, durable, auditable terminal state — never silent disappearance, and never honored late). A holder attempting to approve an `Expired` request gets a clear rejection; the only path forward is for a holder to create a **new** `PendingApprovalRequest`.

This window is deliberately much longer than the 30-second machine-to-machine grant (3.6) — it assumes a human needs to notice a request and act, not a service completing a network call (`research/joint-account-approval-patterns.md` Q3: real systems range from minutes to indefinite; GARBATI's case — a live, customer-initiated debit — sits closer to "a human is actively present" than "a scheduled payment someone will get to eventually," hence 1 hour rather than a multi-day enterprise-style window).

#### 3.8.4 Reconfiguring the threshold (decision #22)

Changing an account's `MandateApprovalPolicy.RequiredApprovals` requires its own consent flow, structurally similar to 3.8.2–3.8.3 but with one deliberate difference: it requires a valid, non-voided `ApprovalRecord` from **every** currently active holder of the account — not merely enough holders to meet the account's *current* threshold.

This is the stricter of the two real-world patterns found in research (`research/joint-account-approval-patterns.md` Q4): multisig systems like Safe only require today's threshold worth of signers to approve a reconfiguration, which would let, e.g., 2 of 3 holders on a "2-of-3" account unilaterally lower it to "1-of-3" without the third holder's knowledge. GARBATI follows the stricter, documented real-world banking-mandate practice instead: a threshold change needs everyone's agreement, specifically to close that exact gap.

A threshold-change request uses the same `PendingApprovalRequest`/`ApprovalRecord` shape with a `Kind` discriminator (`Debit` vs `ThresholdChange`) and `RequiredApprovals` forced to the account's current active-holder count, not the account's current threshold.

#### 3.8.5 Re-validation at the moment the threshold is met (decision #24)

A holder's `ApprovalRecord` is voided immediately — not just on the next read — the moment that holder's mandate is suspended or revoked, whether or not the request has since reached its threshold on paper. Separately, **at the exact instant the `M`-th valid approval would complete the threshold**, IdentityApi re-checks every contributing approver's current mandate status one more time before issuing the grant — not just relying on each approval's state at the moment it was individually given.

This closes a documented real hazard: multisig implementations that do not explicitly re-validate the pending-approval set when an owner's standing changes can let a removed signer's stale approval keep counting (`research/joint-account-approval-patterns.md` Q5). If a live re-check at threshold-completion time finds a now-void approval, the request's `Status` stays `Pending` (not `Approved`) and waits for a replacement approval from a currently-active holder — or expires per 3.8.3.

#### 3.8.6 No caching, applies here too (decision #9)

The "never cache a positive authorization decision" rule (3.1, 4.1) applies to the *final* authorization check performed once the threshold is met — IdentityApi does not pre-approve or cache the outcome while still waiting on approvals. This does not mean the `PendingApprovalRequest`/`ApprovalRecord` rows themselves can't be stored (they must be, to track partial progress) — it means the actual authorize-and-sign decision is always made fresh, at the moment the threshold is satisfied, never reused from an earlier point in the same request's life.

---

## 4. Consistency, performance and availability

### 4.1 Check frequency and caching

Check authorization for each customer-originated money-moving request. **v1 will not cache positive authorization decisions** (decision #9, Confirmed) — this avoids accepting a stale cached approval after a known revocation. The signed grant itself is the short-lived authorization artifact; its 30-second validity is the explicitly accepted window. See 3.8.6 for how this applies to the joint-account approval-collection phase specifically.

Measure end-to-end latency before optimizing. Any future cache requires an explicit staleness/revocation policy rather than an implicit default.

### 4.2 Service failures

Garbati must **fail closed** for customer-originated operations that require IdentityApi authorization (decision #10, Confirmed — matches Saltzer & Schroeder's fail-safe-defaults principle):

| Result | Behaviour |
|---|---|
| Valid positive decision and grant | Orchestrator forwards the transaction and grant. |
| Explicit denial | Stop; do not call LedgerApi for that operation. |
| IdentityApi timeout/unavailable | Do not proceed to LedgerApi. Return a temporary-service failure, not a fabricated denial. |
| Invalid signature, expired grant or transaction mismatch | LedgerApi rejects the request before posting (once #47 is built). |
| LedgerApi timeout after submission | Use the ledger's idempotency/truth-recovery path to determine the outcome; do not blindly issue a new transaction. |
| **(v2)** A joint account's `PendingApprovalRequest` reaches its `ExpiresAt` before enough valid approvals | Status becomes `Expired`. No grant is issued. The requester must open a new request. |
| **(v2)** The `M`-th approval's live re-check (3.8.5) finds a now-voided prior approval | Request stays `Pending`, not `Approved`. No grant is issued. Waits for a replacement approval or expires. |

Bounded retries may be used for retryable errors. An inability to establish authorization must never become permission to transact.

### 4.3 Clock and key management

Because grants expire after 30 seconds, participating services need synchronized clocks and a deliberately small clock-skew allowance.

**4.3.1 Planned rotation.** Signing keys are rotated on a schedule. Publish the current and immediately-previous public key simultaneously, selected by `kid`, until every grant signed with the retiring key has naturally expired — since grants live only 30 seconds, this overlap window is trivially short compared to typical access-token rotation.

**4.3.2 Emergency key compromise (decision #26, new — distinct from 4.3.1).** If a private signing key is suspected stolen, planned-rotation's gradual overlap is the wrong response: the compromised `kid` must stop being trusted **immediately**, not on the next scheduled rotation. IdentityApi and LedgerApi's key-trust configuration must support an emergency "stop trusting this `kid` now" operation, separate from and faster than the rotation procedure in 4.3.1. The exact operational mechanism (a config push, a feature flag, an admin endpoint) is an implementation detail for whoever builds this service; the requirement is that the two procedures (planned rotation vs. compromise response) are documented and exercised as genuinely different paths, not assumed to be the same thing with different timing (`garbati-platform/research/identityapi-design-review.md` flagged this as a real gap against standard key-management guidance).

---

## 5. API surface and service authorization

### 5.1 v1 endpoints

| Endpoint | Purpose |
|---|---|
| `POST /v1/customers` | Create a customer record. Requires a client-supplied `idempotencyKey`, scoped to the calling client and bound to a hash of the request (decision #29) — mirrors LedgerApi's `POST /api/accounts` (decision #43). |
| `GET /v1/customers/{customerId}` | Retrieve an appropriately redacted customer view for an authorized caller. |
| `PATCH /v1/customers/{customerId}/kyc` | Record an authorized KYC status/tier decision. |
| `POST /v1/customers/{customerId}/suspend` | Suspend a customer through a restricted workflow. |
| `POST /v1/customers/{customerId}/reactivate` | Reactivate only after required review and checks. |
| `POST /v1/customers/{customerId}/mandates` | Attach an existing ledger account to the customer with explicit permissions. **(v2: may be called more than once per account, once per holder, for a joint account.)** |
| `GET /v1/customers/{customerId}/mandates` | List mandates for an authorized caller. |
| `GET /v1/accounts/{ledgerAccountId}/mandates` | **(v2, new)** List every active holder and their permissions for a given account — the view a channel needs to show "who else is on this account." |
| `GET /v1/mandates/{mandateId}` | Retrieve mandate status and permitted operations. |
| `POST /v1/mandates/{mandateId}/revoke` | Revoke a mandate and record the actor/reason. |
| `GET /v1/accounts/{ledgerAccountId}/approval-policy` | **(v2, new)** Current `RequiredApprovals` and active-holder count for an account. |
| `POST /v1/accounts/{ledgerAccountId}/approval-policy/change-requests` | **(v2, new)** Propose a new `RequiredApprovals` value. Opens a `ThresholdChange`-kind `PendingApprovalRequest` needing every active holder's consent (3.8.4). |
| `POST /v1/authorization/requests` | **(v2, new)** A holder requests a debit on a joint account. Opens a `Debit`-kind `PendingApprovalRequest`, counting the requester's own approval (3.8.1). For a single-holder account (`RequiredApprovals = 1`) this immediately satisfies the threshold and behaves like v1's `POST /v1/authorization/check`. |
| `POST /v1/authorization/requests/{id}/approve` | **(v2, new)** A different holder approves a pending request. |
| `GET /v1/authorization/requests/{id}` | **(v2, new)** Status of a pending (or resolved) approval request — for the channel to poll or for a holder to check what they're being asked to approve. |

`v1/authorization/check` from the original draft is superseded by `POST /v1/authorization/requests` — the new endpoint subsumes the old one's behavior for the ordinary single-holder case, so there is no separate "simple" endpoint to maintain alongside the joint-account one.

A successful authorization response (once the threshold, however small, is met) provides the signed grant and a unique decision/grant reference. A denial provides a stable reason code without exposing unnecessary sensitive details.

### 5.2 Authorization scopes

- `identity.customer.read`
- `identity.customer.write`
- `identity.kyc.manage`
- `identity.mandate.read`
- `identity.mandate.write`
- `identity.authorization.check` — covers both `POST /v1/authorization/requests` and `.../approve` (no separate scope needed; both are the same customer-facing authorization concern)
- `identity.ops.freeze` — restricted internal scope for the operations/compliance actor and the Orchestrator's outbox-consumption path (2.5); never granted to a channel/payment service by default.

LedgerApi continues to authenticate the Orchestrator as a service using its existing API-key-and-scope mechanism; the signed grant separately proves the integrity and origin of the customer authorization decision.

### 5.3 Which service calls which?

The Orchestrator (or the designated trusted channel/payment service) calls IdentityApi before sending a customer-originated transaction to LedgerApi. LedgerApi does not call IdentityApi in the transaction path. **IdentityApi never calls LedgerApi except the one narrow, read-only account-existence check during mandate setup (2.4)** — every state-changing ledger action, including account freezes, goes through the Orchestrator (decision #17).

---

## 6. Failure, truth, logging and audit

### 6.1 Authorization endpoint returns an error

If `POST /v1/authorization/requests` (or `.../approve`) returns a 500, times out, or cannot provide a valid grant, the Orchestrator must not send the money-moving request to LedgerApi.

### 6.2 Logging rules

Never log: raw BVN or NIN values; identity document images, raw document payloads or full OCR text; API keys, private signing keys, access tokens or complete authorization-grant tokens; complete request bodies that may contain personal or verification data; unnecessary sensitive identity information.

Prefer structured operational identifiers: `CustomerId` (opaque but still access-controlled), `MandateId`, `DecisionId`, `PendingApprovalRequestId`, operation, reason code, outcome, elapsed time, correlation ID.

**The log/audit store itself is a high-value target and needs its own access control**, not just "don't put sensitive fields in it" (`garbati-platform/research/identityapi-design-review.md` flagged this as implied but unstated in v1 — now stated explicitly).

### 6.3 Audit events

Keep durable audit events separate from diagnostic application logs (decision #13, Confirmed). Audit at least:

- Customer creation and privileged identity-data changes.
- KYC verification outcomes and tier changes.
- Customer suspension/reactivation, and the `Closed`/`Deceased` cascade specifically (2.1).
- Mandate creation and revocation.
- **(v2)** Every `PendingApprovalRequest` lifecycle event: created, each approval given, voided (and why), expired, approved/grant issued.
- **(v2)** Every threshold-change request and its outcome.
- Privileged overrides, including actor and reason.
- Authorization decisions, with the grant/decision ID and outcome.
- Freeze propagation attempts and acknowledgements (2.5's outbox).

Audit events are append-only from ordinary application code, access-restricted and retained according to approved policy. Correlation IDs connect diagnostics, authorization decisions and ledger outcomes without replacing the unique decision/grant ID.

---

## 7. Data and storage

### 7.1 Tables

| Table | Responsibility |
|---|---|
| `Customers` | Internal customer identity, current operational status, KYC state/tier and timestamps. |
| `CustomerIdentityData` | Sensitive identity/contact fields, with stricter access and encryption. |
| `KycVerifications` | Append-only verification attempts/results and provider references. |
| `KycTierPolicies` | Configurable jurisdiction/tier rules with effective dates. |
| `Mandates` | Customer-to-ledger-account relationship, state, creation and revocation details. Unique partial index on `(CustomerId, LedgerAccountId) WHERE Status = 'Active'` — **not** a unique index on `LedgerAccountId` alone, which would make joint accounts impossible. |
| `MandatePermissions` | Operations allowed by each mandate. |
| `MandateApprovalPolicies` | **(v2, new)** One row per `LedgerAccountId`: `RequiredApprovals` and change history. |
| `PendingApprovalRequests` | **(v2, new)** Debit and threshold-change approval requests in flight or resolved, with `Kind`, `Status`, `ExpiresAt`. |
| `ApprovalRecords` | **(v2, new)** One row per holder's approval (or void) against a `PendingApprovalRequest`. |
| `AuditEvents` | Durable history of important security and business actions. |
| `OutboxMessages` | Durable work, such as account-freeze propagation to the Orchestrator, with retry and completion status. |

### 7.2 Identity documents and sensitive data

Unchanged from v1 (decision #12, Confirmed): no raw KYC document images in ordinary relational columns by default. Prefer references to a secured object store or external verification provider; store only the outcome and metadata required for authorization.

### 7.3 Immutable versus mutable data

- Customer status and current KYC tier change through explicit, audited transitions.
- KYC verification attempts/results are preserved as historical records.
- A mandate transitions from active to revoked; the revocation evidence remains available.
- **(v2)** A `PendingApprovalRequest` and its `ApprovalRecord`s are never deleted — `Expired`/voided states are terminal facts, not rows to clean up.
- Audit events are append-only from normal application code.

### 7.4 Cross-service integrity

IdentityApi and LedgerApi have separate databases; no database foreign key crosses services. Enforce within IdentityApi, with a real database constraint (not application validation alone — decision #19), that the one-active-mandate-per-`(customer, account)` rule cannot be violated under concurrent requests.

---

## 8. Reversal authority — explicitly NOT designed yet

**Do not infer a design for this from the rest of the document.** Who may initiate a reversal, what reason codes or approvals are required, the expected operational response time, and how reconciliation handles an external effect a ledger reversal cannot itself undo (e.g. the recipient already withdrew the money elsewhere) are all **open**, tracked as `garbati-platform` **T-015**. The original v1 draft's Section 3.6/9 flagged this as unresolved; v2 has not resolved it either — it was deliberately deferred by the owner (16 Oct 2026) to its own dedicated design pass rather than a quick answer folded into this document. `research/identityapi-design-review.md` flags this as the one item most clearly tied to real financial-loss risk if left unresolved at build time — build T-015 before relying on reversal as a recovery path for anything beyond what LedgerApi's own existing chain-reversal mechanics (decision #45) already guarantee at the ledger level.

---

## 9. Decisions log

Numbering continues from the original v1 draft (1–18). New v2 decisions are 19 onward. "Confirmed" means the owner has explicitly ratified this; several were also checked against published standards before being confirmed (cited inline).

| # | Decision | Options considered | Decision and status |
|---|---|---|---|
| 1 | Platform name | Mizan; Garbati | **Confirmed: Garbati.** |
| 2 | Service boundary | Identity data in LedgerApi; separate service | **Confirmed:** separate `IdentityApi`, consistent with LedgerApi decision #12. |
| 3 | Transaction flow | Ledger calls IdentityApi; trusted caller obtains grant first | **Confirmed:** Channel → Orchestrator → IdentityApi → Orchestrator → LedgerApi. |
| 4 | Authorization evidence | Plain allow/deny response; signed transaction-bound grant | **Confirmed:** asymmetric signed authorization grant. |
| 5 | Grant content | General customer approval; exact transaction binding | **Confirmed:** bind operation, accounts, amount, currency, customer, mandate and idempotency/intent identifier; schema finalized in 3.3/3.5. |
| 6 | Grant expiry | Longer lifetime; short lifetime | **Confirmed:** 30 seconds from issuance. Matches the right order of magnitude for a single-use, machine-to-machine, transaction-bound grant (no standard source gives an exact number for this category; `research/identityapi-design-review.md` Q1). |
| 7 | Revocation race | Immediate global invalidation; short outstanding-grant window | **Confirmed:** accept the short in-flight window (≤30s); deny new grants after revocation. This is the standard RFC 7009 trade-off for self-contained tokens (`research/identityapi-design-review.md` Q2). |
| 8 | Grant verification | Trust Orchestrator; Ledger calls IdentityApi; Ledger verifies locally | **Confirmed:** Orchestrator may verify; LedgerApi independently verifies locally once built (see #35, D-6). No direct runtime call from LedgerApi to IdentityApi. Matches RFC 9068's local-JWT-validation pattern. |
| 9 | Caching | Cache positive authorization; live grant check | **Confirmed, 16 Oct 2026: no positive-decision caching in v1.** |
| 10 | Service outage | Fail open; fail closed | **Confirmed, 16 Oct 2026: fail closed** for customer-originated operations. Matches Saltzer & Schroeder's fail-safe-defaults principle. |
| 11 | KYC tiers | Hardcoded regional rules; configurable policy | **Confirmed, 16 Oct 2026: configurable policy data.** Actual Nigerian limits still need compliance confirmation. |
| 12 | KYC documents | Store raw files in SQL; external secure store/reference | **Confirmed, 16 Oct 2026: external secured storage/references.** |
| 13 | Audit history | Ordinary logs only; separate durable audit trail | **Confirmed, 16 Oct 2026: separate append-only audit events**, distinct from diagnostic logs. |
| 14 | Customer suspension/closure/death effects | Change status only; cascade to mandates/accounts | **Confirmed, 16 Oct 2026, superseding v1's single "Suspended" treatment:** see 2.1's full table — `Closed`/`Deceased` auto-revoke every mandate and freeze every solely-held account; `Suspended` denies new grants only, freeze is a separate explicit step. |
| 15 | Customer authentication | IdentityApi owns login; channel owns login | **Confirmed, 16 Oct 2026: channel owns login/session.** IdentityApi never handles passwords. |
| 16 | Customer identifier | Government ID; account number; internal ID | **Confirmed, 16 Oct 2026: internal `CustomerId`**, generated by IdentityApi, carried by the Orchestrator through account creation (2.4) — LedgerApi never learns it. |
| 17 | Freeze ownership | IdentityApi calls LedgerApi directly; goes through the Orchestrator | **Confirmed, 16 Oct 2026: through the Orchestrator**, via a durable outbox (2.5). Matches `ARCHITECTURE.md` P-1 — the Orchestrator is the single gate for every ledger-state-changing action; no exception for IdentityApi. |
| 18 | Repository/service name | `IdentityApi`; `MandateApi` | **Confirmed, 16 Oct 2026: `IdentityApi`** — covers both identity and mandate responsibilities. |
| 19 | Mandate model | Single customer only; multiple holders (joint accounts) | **Confirmed, 16 Oct 2026, reversing the v1 recommendation: joint accounts are in v1 scope.** Multiple active mandates per account, one per holder, enforced as one-active-mandate-per-`(customer, account)` via a real database constraint (not application-only), not one-active-mandate-per-account. |
| 20 | Joint-account approval threshold semantics | Simple unweighted M-of-N; weighted votes; named-required-approvers | **Confirmed, 16 Oct 2026: simple unweighted "any M of N."** No weighted votes or named-required-approvers in v1. Matches the standard default found across banking mandate types, bare multisig, and basic enterprise approval tiers. |
| 21 | Does the requesting holder count toward the M required approvals? | Initiator excluded (maker-checker style); initiator can count | **Confirmed, 16 Oct 2026: the initiator can count** as one of the M. (Note: this is the simpler multisig-style choice, not the stricter maker-checker convention some banking/treasury systems use — a deliberate, informed choice, not an oversight.) |
| 22 | Threshold reconfiguration consent | Only today's threshold worth of holders (multisig/Safe style); every current holder (banking-mandate style) | **Confirmed, 16 Oct 2026: every current holder must consent.** Closes the gap where a subset of holders meeting today's bar could unilaterally weaken the account's protection without every holder's knowledge. |
| 23 | Pending joint-approval timeout | A range from minutes to indefinite was researched | **Confirmed, 16 Oct 2026: 1 hour**, with an explicit, durable `Expired` terminal state. No late-honoring after expiry; a holder must open a new request. |
| 24 | Stale partial-approval handling | Keep an approval valid once given; void it on the approver's status change | **Confirmed, 16 Oct 2026: void immediately**, plus a live re-check of every contributing approver at the exact moment the threshold is met (not just when each approval was originally given). Closes a documented real hazard seen in multisig implementations. |
| 25 | Approval single-use/replay | Reusable across requests; bound to one request only | **Confirmed, 16 Oct 2026: single-use, request-bound** — the same no-replay principle already established for the machine-issued grant (#7), applied one layer earlier to the human approval step. |
| 26 | Key-compromise procedure | Assume covered by planned rotation; document as a separate, faster procedure | **Confirmed, 16 Oct 2026: a separate, explicit "stop trusting this `kid` immediately" procedure, distinct from scheduled rotation (4.3.2).** Exact operational mechanism is an implementation detail for the builder. |
| 27 | Decision-token verification timing (LedgerApi decision #47) | Build it before anything ships; ship the demo without it first | **Confirmed (`garbati-platform` D-6), 16 Oct 2026: ship the demo without it, documented as a known limitation, build #47 once OrchestratorApi exists.** IdentityApi still issues real signed grants from day one — only LedgerApi's verification step is deferred. |
| 28 | Reversal authority, reason codes, reconciliation | Decide now; defer to a dedicated task | **Deferred, 16 Oct 2026, to `garbati-platform` T-015** — not a decision made in this document. See Section 8. |
| 29 | Idempotency on customer creation | None; client key + request hash | **Confirmed, 16 Oct 2026: client-supplied `idempotencyKey` + request hash, mirroring LedgerApi decision #43.** `POST /v1/customers` requires an `idempotencyKey`, scoped to the calling client; a retry with the same key and a matching hash of the other fields returns the originally-created customer; the same key with a different hash is rejected (409). | OrchestratorApi's own design (`Garbati.OrchestratorApi/docs/DESIGN.md` Section 1.4) already assumed this guarantee exists — it derives `orch:{operationId}:create-customer` as a retry-safe key for calling this endpoint. Without it, a timed-out create-customer call that gets retried could create a duplicate customer. This was deliberately left out of T-016's first build as an open question (see that PR) rather than assumed; the owner has now confirmed it belongs here. **Numbering note (corrected 17 Oct 2026):** this document's body prose (Sections 1–7) previously cited decision numbers that didn't match this table — a pre-existing authoring error from when this table was first written, not something introduced by T-016's PR. Every body citation has been corrected to point at the table row it actually describes; this is the only decision this document assigns the number 29 to. |

---

## 10. Open questions and next steps

Resolve these before production use; some can remain implementation tasks for the first build iteration.

1. **Reversal authority (T-015).** See Section 8. The single highest-priority open item against real financial-loss risk.
2. **Financial limits.** Which component authoritatively enforces per-transaction and aggregate/daily limits? Any concurrent aggregate limits must be enforced with appropriate atomicity.
3. **KYC provider and Nigerian tier limits.** Which provider or manual workflow supplies verification results? The actual tier evidence/limits still need compliance confirmation (`research/nigerian-regulatory-requirements.md`).
4. **Grant contract finalization.** Canonical amount encoding, operation names, token type/version, clock-skew policy and error codes — ready to implement per Section 3, but not yet exercised against real signing keys.
5. **Channel identity mapping.** Define how authenticated channel identities map to the internal `CustomerId` so callers cannot choose another customer's identity by editing request fields. This is a textbook **BOLA** (Broken Object Level Authorization) risk — OWASP's #1-ranked API security risk — and is correctly flagged as open, not solved, per `research/identityapi-design-review.md` Q5.
6. **Performance.** Set and test latency targets, timeout budgets and bounded retry policies, including validating the 30-second grant window against real measured latency before finalizing it (`research/identityapi-design-review.md` Q1).
7. **Retention and privacy.** Approve encryption, access controls, retention, correction and deletion rules for identity information and verification evidence.
8. **Deceased joint holder's share.** 2.1 explicitly does not auto-freeze a joint account when only one of several holders dies — estate/succession handling for that holder's share is an operations/legal process not designed here.
9. **Sharia confirmation on joint-account equal ownership (AAOIFI).** AAOIFI's Sharia standards describe a joint account as equally owned by its holders by default unless they agree otherwise. Whether a disproportionate approval scheme (e.g. one holder able to act alone on an account with unequal contributions) is appropriate for a Sharia-compliant GARBATI account is a question for a qualified Sharia scholar/board — not resolved by this document or by `research/joint-account-approval-patterns.md`, which only flagged it.
10. **CBN GSI confirmation on joint-account signatory identification.** CBN's Global Standing Instruction guidelines require identifying and obtaining evidence for all signatories to a joint account, and executing GSI mandates with all signatories' execution. No CBN-specific rule on approval-*threshold mechanics* was found; a compliance officer must confirm before production.
11. **Token passthrough / confused-deputy naming.** The `aud` claim and LedgerApi's audience check (once #47 is built) are the right defense against a grant meant for one consumer being accepted by a different one, should the platform ever run more than one LedgerApi-equivalent or Orchestrator — name this explicitly in that future design rather than relying on `aud` being understood as the full mitigation (`research/identityapi-design-review.md` Q5).

---

## Appendix A. Security acceptance tests

The v2 design should not be considered complete until automated tests prove at least the following:

**From v1 (unchanged):**
- A correctly signed, unexpired grant matching the request passes grant validation.
- A grant signed with an untrusted key, or an untrusted/unpinned algorithm, is rejected.
- Any changed account, amount, currency, operation or idempotency key causes rejection.
- A grant for a different audience or issuer is rejected.
- A grant older than 30 seconds is rejected, accounting only for a deliberately small clock-skew allowance.
- A repeated identical request with the same idempotency key does not post money twice.
- Reusing a grant for a different request is rejected.
- A revoked mandate cannot obtain a new positive grant.
- If IdentityApi times out or fails, the Orchestrator does not send an unapproved customer transaction to LedgerApi.
- A posted transaction can be traced to its authorization decision ID, correlation ID and ledger transaction reference without logging the grant token or sensitive identity data.

**New for v2:**
- `Closed` or `Deceased` status automatically revokes every mandate and queues a freeze for every account where it was the customer's only active mandate; a joint account with surviving holders is not frozen.
- Two active mandates on the same account (two holders) is accepted; a second active mandate for the *same* holder on the same account is rejected by the database constraint, not just application code.
- A single-holder account's debit request is satisfied by one approval (the requester's own), exactly matching pre-v2 behavior.
- A 2-of-3 account's debit request is not satisfied by one approval, is satisfied once a second distinct holder approves, and the requester's own approval is one of the two counted.
- A `PendingApprovalRequest` that reaches its 1-hour `ExpiresAt` without enough approvals moves to `Expired`; an approval submitted after that point is rejected; a new request must be created.
- A threshold-change request on a 2-of-3 account is rejected if only 2 of the 3 holders approve; it succeeds only once all 3 have.
- A holder who approves, is then suspended before the threshold is met, does not count toward the threshold even if their `ApprovalRecord` predates the suspension.
- A holder who approves, is then suspended *after* the threshold was already met but *before* the grant was actually issued, is caught by the live re-check at threshold-completion time, not just by the original per-approval check.
- An approval given for one `PendingApprovalRequest` cannot be submitted against, or counted toward, a different one, even for the identical amount/accounts.
- An emergency key-compromise response (4.3.2) can take effect without waiting for the next scheduled rotation window.

---

## Appendix B. T-016 build notes (project skeleton, Customer, KYC)

This appendix records what the first build task (`garbati-platform` T-016) actually built against this
document, and the implementation judgment calls it had to make where this document does not spell out
every detail. It does not change any ratified decision above. Per `AGENTS.md`, new decision numbers are
assigned by the coordinator, not by the builder; the items below are proposed as `#TBD-n` for that.

**Built:** `Customer`, `CustomerIdentityData`, `KycVerification`, `KycTierPolicy` (with a placeholder
Nigerian seed, clearly marked as such), `AuditEvents`, `OutboxMessages`. Four endpoints from Section 5.1
(`POST /v1/customers`, `GET /v1/customers/{customerId}`, `PATCH /v1/customers/{customerId}/kyc`,
`POST /v1/customers/{customerId}/suspend`, `POST /v1/customers/{customerId}/reactivate`) plus two this
task added (see `#TBD-1`). API-key auth with the `identity.customer.read`/`identity.customer.write`/
`identity.kyc.manage` scopes from Section 5.2 (the full scope list from Section 5.2 is defined in code
now, but only these three are wired to an endpoint yet). **Not built** (by explicit scope, deferred to
T-017): `Mandates`, `MandatePermissions`, the joint-account entities, `/v1/mandates/*`,
`/v1/authorization/*`, and any grant-signing code.

- **`#TBD-1` (endpoint naming).** Section 5.1 lists only `suspend`/`reactivate`; it does not spell out
  separate endpoints for becoming `Closed` or `Deceased`, even though Section 2.1's status table treats
  them as distinct, terminal, irreversible states with their own cascade. T-016 added
  `POST /v1/customers/{customerId}/close` and `POST /v1/customers/{customerId}/mark-deceased` rather than
  overloading `suspend` for them. Proposed for the coordinator to ratify or rename.
- **`#TBD-2` (repeat Closed/Deceased attempt).** Section 2.1 does not say what happens if `close` or
  `mark-deceased` is called on a customer already in that state. T-016 chose **409**, matching LedgerApi's
  own convention for a repeated terminal operation (`AlreadyReversedException` → 409), over a silent
  no-op, so a second call carrying a different reason is never silently dropped. `suspend`/`reactivate`
  remain idempotent (a repeat from the same status is a no-op, 200), since those are not terminal.
- **`#TBD-3` — resolved by decision #29 (Section 9).** T-016 originally built `POST /v1/customers`
  *without* an idempotency key, raising it as an open question rather than silently adding one. The owner
  has since confirmed it belongs here (OrchestratorApi's own design already assumed the guarantee exists);
  `idempotencyKey` is now required, with the same client-scoped-key-plus-request-hash mechanics as
  LedgerApi's `POST /api/accounts` (decision #43). See decision #29.
- **`#TBD-4` (`CustomerIdentityData` fields).** Kept to `FullName`/`Email`/`PhoneNumber` only — no BVN/NIN
  column. Section 6.2's "never log raw BVN/NIN" rule implies the same care is needed for *storage*
  (encryption at rest), which has not been decided yet; rather than guess at cryptography, that column is
  left out entirely pending a follow-up decision (likely needed by T-017's KYC evidence work).
- **`#TBD-5` (Closed/Deceased cascade, decision #14 — the known gap).** T-016 builds the status
  transition, its audit event, and the target *shape* of the freeze outbox message
  (`Contracts.Outbox.AccountFreezeRequestedPayload`), but **not** the mandate lookup itself — `Mandates`
  does not exist until T-017. `CustomerService.CascadeMandateRevocationAndFreezeAsync` is a documented
  no-op today; no mandate is revoked and no `OutboxMessages` row is written on this path yet. T-017 must
  come back to that exact method. See the T-016 pull request for the full note.

**Also fixed (QA review of the T-016 PR):** `suspend`/`reactivate`/`close`/`mark-deceased` all require a
non-empty `Reason` now (`CustomerStatusChangeRequestValidator`, 400 if missing) — the request's own
comment already claimed this, but nothing enforced it until QA caught the gap. Section 6.3 expects these
restricted workflows' audit record to carry "actor and reason," so a reason that can be silently omitted
defeats that.
