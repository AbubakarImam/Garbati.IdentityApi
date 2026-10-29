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

Sensitive identity fields are separated from ordinary operational fields (`CustomerIdentityData`, 7.1), protected by least-privilege access and an explicit retention policy. **Bvn/Nin specifically (T-044, decision #26, `garbati-platform` D-26) are stored unencrypted for now** - not a blanket statement that every field on this table is unencrypted by design, but the actual, deliberate posture for these two fields after research found no specific NDPA/CBN mandate requiring encryption, only a general risk-based duty (`garbati-platform/research/ndpa-encryption-requirement.md`). Revisiting encryption later remains open.

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

**T-049 (`garbati-platform` D-16), 28 Oct 2026: a retried *denial* now dedupes; a retried *approval* still does not, deliberately.** A client retry of `RequestAuthorizationAsync` that results in approval always mints a fresh `DecisionId` and grant, exactly as before — there is nothing persisted to replay (decision #13: the signed grant itself is never stored), and LedgerApi's own idempotency constraint (`ix_transactions_initiated_by_idempotency_key`) is the real money-safety backstop regardless of how many grants IdentityApi issues for the same logical request. A retried *denial*, however, is pure waste with no money-safety angle either way: before this fix it wrote a second, independent `AuthorizationDecision` row and a second `AuthorizationDenied` audit event for one logical request. `PersistDecisionAsync` now resolves this the same way `CustomerService.CreateCustomerAsync` resolves a reused idempotency key: a new partial unique index on `(RequestedBy, IdempotencyKey) WHERE Approved = false` lets the insert itself detect the duplicate; on conflict, the existing denied row is looked up, its canonical request fields are compared against the new request, and either the original decision is returned (same request) or `DuplicateIdempotencyKeyException` is thrown (409, a different request reusing the same key). See Appendix I.

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
- Transaction intent: `Operation`, `DebitAccountId`, `CreditAccountId`, `Amount`, `Currency`, `IdempotencyKey` — the same canonical fields that will eventually appear in the grant (3.3), fixed at request-creation time so a later approval cannot be tricked into approving a changed transaction. This is deliberate and has no edit path today — pinned in code by `JointApprovalNoEditRegressionTests` (Appendix J, `garbati-platform` T-053/D-29).
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

#### 3.8.7 Listing pending requests (T-031, `garbati-platform` D-15)

`GET /v1/authorization/requests?status=&pageSize=&pageToken=` lists `PendingApprovalRequest` rows for a given `Status` (`Pending` by default — the useful case for an approvals queue; `Approved`/`Expired`/`Cancelled` are accepted too, for completeness, with no extra business logic beyond the status filter). Same scope as the rest of this controller (`identity.authorization.check`) — no new scope.

**This endpoint is specifically "pending joint-approval requests," not "every authorization decision ever made."** A single-holder (`RequiredApprovals <= 1`) request never creates a `PendingApprovalRequest` row at all — 3.8.1's whole branch is that it resolves synchronously via the ordinary `AuthorizationDecision` path instead. A caller must not expect single-holder transfers to show up here; `GET /v1/authorization/requests/{id}` (by id) remains the only way to check a single-holder decision's status.

**Ordering and paging.** Oldest-first (ascending internal `Id`, which is monotonic with creation order — not the public `RequestId`, which is a random identifier with no ordering meaning). `pageSize` defaults to 20, maximum 100 (validated in the controller, 400 outside that range). `pageToken` is an opaque cursor — base64 of the last row's internal `Id` on the previous page; a malformed token is a 400 (`InvalidPageTokenException`), not a 404. The response is `{ items: [...], nextPageToken: string | null }` (`PagedResponse<T>`) — checked against `LedgerApi` and `Garbati.OrchestratorApi` first; as of 19 Oct 2026 neither has any list endpoint yet, so there was no existing platform convention to reuse. This shape is intentionally generic so the next list endpoint on any Garbati service can reuse it.

**Deliberately not done:** no lazy-expiry pass over listed rows (unlike the by-id `GetPendingRequestAsync`) — the task explicitly scopes this to a `WHERE Status = @status` filter, and running expiry on every row of every page would be exactly the kind of extra business logic that was ruled out, plus it would make a read mutate data depending on how many rows on the page happen to be stale. A request already past its `ExpiresAt` keeps showing as `Pending` here until something else (a by-id `GET`, or an approve attempt) touches it individually — a known, documented limitation, not a bug.

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
| `PATCH /v1/customers/{customerId}/details` | **(T-035, new)** Partial update of `CustomerIdentityData`'s `FullName`/`Email`/`PhoneNumber` - every field optional, only the supplied ones change. Same `identity.customer.write` scope as `POST /v1/customers`; no new scope. The endpoint `OpsApi`'s T-034 customer-detail-edit maker-checker workflow calls once a staged change is approved. |
| `PATCH /v1/customers/{customerId}/identity-numbers` | **(T-044, new)** Set-once submission of `CustomerIdentityData.Bvn`/`.Nin` - at least one of the two required per request; a field already set rejects a *different* resubmitted value with 409, and accepts the *same* one idempotently. Deliberately its own endpoint, not folded into `.../details` above - identity numbers are not freely editable the way a name/email/phone is. Scope `identity.kyc.manage`, not `identity.customer.write`: this is part of the KYC evidence pipeline (see Appendix H), not general contact-detail editing. |
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
| `GET /v1/authorization/requests?status=&pageSize=&pageToken=` | **(T-031, new)** A browsable, paged queue of **joint-approval** `PendingApprovalRequest` rows for a given `Status` (defaults to `Pending`), oldest-first. Scoped specifically to joint-approval requests — a single-holder (`RequiredApprovals <= 1`) request resolves synchronously via `AuthorizationDecision` and never creates a row here, so it never appears in this list. See Section 3.8.7. |

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
- `identity.ops.reversal` — restricted internal scope for the ops channel's reversal-authorization calls (Section 8, decision #30); never granted to a customer-facing channel.

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
- **(T-044)** Identity-number submission (`IdentityNumberSubmitted`) - which of `Bvn`/`Nin` changed, never the submitted value itself; a set-once no-op (resubmitting the already-stored value) writes no row (Appendix H).

Audit events are append-only from ordinary application code, access-restricted and retained according to approved policy. Correlation IDs connect diagnostics, authorization decisions and ledger outcomes without replacing the unique decision/grant ID.

---

## 7. Data and storage

### 7.1 Tables

| Table | Responsibility |
|---|---|
| `Customers` | Internal customer identity, current operational status, KYC state/tier and timestamps. |
| `CustomerIdentityData` | Sensitive identity/contact fields, with stricter access. Includes `Bvn`/`Nin` (T-044) - stored unencrypted for now (decision #26), not encrypted like the table's own earlier, now-outdated general description claimed. |
| `KycVerifications` | Append-only verification attempts/results and provider references. |
| `KycTierPolicies` | Configurable jurisdiction/tier rules with effective dates. |
| `Mandates` | Customer-to-ledger-account relationship, state, creation and revocation details. Unique partial index on `(CustomerId, LedgerAccountId) WHERE Status = 'Active'` — **not** a unique index on `LedgerAccountId` alone, which would make joint accounts impossible. |
| `MandatePermissions` | Operations allowed by each mandate. |
| `MandateApprovalPolicies` | **(v2, new)** One row per `LedgerAccountId`: `RequiredApprovals` and change history. |
| `PendingApprovalRequests` | **(v2, new)** Debit and threshold-change approval requests in flight or resolved, with `Kind`, `Status`, `ExpiresAt`. |
| `ApprovalRecords` | **(v2, new)** One row per holder's approval (or void) against a `PendingApprovalRequest`. |
| `AuditEvents` | Durable history of important security and business actions. |
| `OutboxMessages` | Durable work, such as account-freeze propagation to the Orchestrator, with retry and completion status. |
| `ReversalOutcomes` | One row per reversal authorization decision once its ultimate outcome is known (`Completed`/`WriteOff`) — a thin audit record of the recovery decision's conclusion, not the recovery process itself (Section 8.6). |

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

## 8. Reversal authority (decisions #30–#35)

**Status: designed, 18 Oct 2026** (`garbati-platform` T-015, informed by `research/reversal-authority-patterns.md`). This closes the gap v1 and v2 both left open. It deliberately does not touch the already-fixed **ledger mechanics** (`ARCHITECTURE.md` D-7, LedgerApi decision #45: all-or-nothing chain reversal, last leg first, no remediation path if a leg can't be undone) — it is entirely about the layer above that: who may ask IdentityApi to authorize a reversal, and what IdentityApi does with that request.

### 8.1 Where this sits architecturally — the key simplification

A reversal request reaches IdentityApi **only from a trusted ops channel** — never from a customer-facing channel, and never directly from a customer. This is the same "customers never call IdentityApi directly" principle that governs every other flow (Channel → Orchestrator → IdentityApi), narrowed further here: specifically the *ops* channel, authenticated as its own service identity, is the only permitted caller for a reversal authorization.

Whatever maker-checker / dual-control discipline that ops channel enforces internally — who may request a reversal, who must approve it, what evidence they review — happens **before** it ever calls IdentityApi, inside tooling this document does not design and that does not exist yet. IdentityApi trusts that decision has already been made, exactly the way it already trusts the Orchestrator's own caller authentication for every other operation. This is a deliberate choice **not** to build a second approval-collection system alongside T-018's joint-account workflow — reversal authorization stays a simple, synchronous, single-call decision, like a single-holder (`M=1`) transfer request was before T-018 added the multi-holder case.

**Decision #30.** Options considered: (a) build dual-control/maker-checker for reversals inside IdentityApi, mirroring T-018's `PendingApprovalRequest` workflow; (b) trust a single, restricted ops-channel caller and let that caller's own tooling handle approval. **Confirmed: (b).** A new scope, `identity.ops.reversal`, gates the endpoint; it is never granted to a customer-facing channel's service identity.

### 8.2 Reversal bypasses the account's own mandate entirely

A reversal authorization does **not** check the account's `MandateApprovalPolicy`, does not care whether the account is single-holder or joint, and does not require any mandate holder's approval — regardless of what the original transaction needed.

**Decision #31.** Options considered: inherit the account's own `RequiredApprovals` threshold; require a lower bar (e.g. any one holder, since a reversal restores money rather than removing it); an ops override that bypasses the mandate model entirely. `research/reversal-authority-patterns.md` Q2 found no standard source answering this directly — a genuine gap, not a settled industry practice. **Confirmed: ops override.** The ops channel's own process (8.1) is the real gate; re-checking the customer's mandate would just duplicate a decision already made upstream, for a caller (ops) the mandate model was never designed to govern in the first place.

### 8.3 What IdentityApi actually validates

Given 8.1/8.2, there is no `PendingApprovalRequest`-equivalent for reversals. `POST /v1/authorization/requests` accepts a new `operation` value, `"Reversal"`, and the endpoint:

- Confirms the caller holds `identity.ops.reversal` (checked the same way every other scope is, `AuthorizationController`/the existing policy infrastructure).
- Requires a reference to the original transaction/chain (whatever identifier the Orchestrator and LedgerApi already use) — IdentityApi does not independently verify this reference exists at LedgerApi; it trusts the caller's assertion, the same pattern already used for `LedgerAccountId` at mandate attachment (2.4) and left as a `TODO` there for the same reason (LedgerApi has nothing to call yet).
- Requires a reason code from the fixed list in 8.4.
- Carries the same canonical fields every grant already binds (8.5): the original transaction reference in place of debit/credit accounts, amount/currency if meaningful for the reason code, idempotency key.

### 8.4 Reason codes

**Decision #32.** Reusing converged industry vocabulary (`research/reversal-authority-patterns.md` Q3 — ISO 20022 and the card networks already agree on the same handful of categories) instead of inventing GARBATI-specific names where a standard one exists:

| Code | Meaning |
|---|---|
| `DUPL` | Duplicate posting |
| `TECH` | Technical/processing error |
| `FRAD` | Fraudulent origin |
| `CUST` | Customer requested, no error claimed — the ops channel's own process is responsible for confirming the recipient's cooperation before calling this endpoint; IdentityApi does not enforce that |
| `UNAUTH` | Customer disputes the transaction as never authorized by them (GARBATI-specific — no single ISO 20022 code maps to a consumer-facing claim; ISO 20022's codes are bank-to-bank message codes) |

**`DISPUTE`** (a card-network-style "consumer dispute" category, for a future financing/merchant product) is explicitly **not** defined yet — the owner chose to add it only once that roadmap item actually exists, not speculatively now.

### 8.5 Grant issuance

**Decision #33.** A reversal authorization reuses the existing signed-grant machinery (3.3, `GrantSigner`) exactly — same claim shape, same 30-second expiry, same ES256 signing — with `operation: "Reversal"` and the reason code (8.4) as an additional claim. Options considered: a reversal-specific grant/token shape; reuse the existing grant shape with a new operation value. **Confirmed: reuse.** Nothing about `GrantSigner`, the key-management guard, or the claim-verification logic changes; only the set of valid `operation` values grows.

### 8.6 Outcome recording, including write-off

**Decision #34.** IdentityApi records every reversal authorization decision the same way it records any other (`AuthorizationDecision`) — and, in addition, tracks the reversal's **ultimate outcome** once it's known, including a distinct write-off event when the outcome is "could not be completed, money unrecoverable" (matching `research/reversal-authority-patterns.md` Q4's real-world distinction between a recoverable claim and a loss formally written off).

Since IdentityApi never executes a reversal itself (the Orchestrator/LedgerApi do, per decision #17 — the Orchestrator is the single gate for every ledger-state-changing action), IdentityApi only *learns* the outcome once told — by a follow-up call from the ops channel (or the Orchestrator) reporting what LedgerApi's chain-reversal actually did.

New entity, `ReversalOutcomes`: `AuthorizationDecisionId` (the original reversal grant's decision), `Outcome` (`Completed` / `WriteOff`), `RecordedAt`, `RecordedBy`, `Notes` (nullable). This is deliberately a **thin audit record**, not a collections/lien workflow — the actual recovery process (demand notices, watch-listing, escalation to law enforcement) stays entirely outside IdentityApi; `ReversalOutcomes` only records that a decision about recoverability was made and by whom, not how the recovery attempt itself was run.

Options considered: no outcome tracking in IdentityApi at all (leave it fully to ops tooling, outside this service); track a distinct write-off event as above. **Confirmed: track it**, per the owner's explicit choice over the research's more conservative "fully out of scope" recommendation.

**Tested and confirmed, 18 Oct 2026 (`garbati-platform` T-021, `research/lien-auto-sweep-patterns.md`):** the owner asked whether real systems instead place a standing lien that automatically sweeps the owed amount the moment the account is later funded, rather than deciding `Completed`/`WriteOff` once. Research found the opposite default everywhere checked — an ordinary lien is a point-in-time snapshot that does not reach into future deposits; reaching forward in time always requires a separate, specially-authorized mechanism (a continuing garnishment order, or a card issuer's advance-consented, regulator-capped recovery of *its own* extended credit), never a default property of a lien. **The two-state design above stands, unchanged**, confirmed against real practice rather than merely asserted.

### 8.7 Response time

**Decision #35.** No code-enforced SLA anywhere in this flow. IdentityApi timestamps every reversal request, decision, and outcome exactly as it already timestamps every other authorization decision — that is the only "response time" artifact it produces. A real operational SLA (e.g. the ~2-business-day instant-EFT dispute standard `research/reversal-authority-patterns.md` Q5 found in CBN's framework) is a target the ops process is measured against, not a rule that makes IdentityApi reject an old reversal request. Options considered: reject a reversal request past some age limit; no code enforcement. **Confirmed: no code enforcement.**

### 8.8 What this still does not settle

- **The ops channel itself** — its maker-checker UI/process, how it authenticates its own internal users, what evidence it requires before calling IdentityApi — does not exist and is not designed here. It is out of GARBATI's current scope entirely, tracked nowhere yet.
- **The actual recovery process** behind a `ReversalOutcomes` row — demand notices, watch-listing, legal/criminal referral, when the recoverable phase formally ends and becomes a write-off — is not designed. Section 8.6's entity is only the audit record of that process's conclusion, not the process.
- **CBN-specific timing figures** in `research/reversal-authority-patterns.md` (instant-EFT dispute windows, lien duration, watch-listing timelines) are flagged there as needing compliance confirmation against the live circular before any real SLA relies on them — not re-verified here.
- **Sharia framing of a write-off.** `research/reversal-authority-patterns.md` flags Qard Hasan (an interest-free benevolent loan) and Ibra (a voluntary debt rebate) as Islamic-finance concepts potentially relevant to how a `WriteOff` outcome should be framed, especially toward a customer in genuine hardship rather than a fraud actor. Not resolved here — a qualified Sharia scholar must weigh in before this is presented to a customer as final, not just recorded internally.

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
| 28 | Reversal authority, reason codes, reconciliation | Decide now; defer to a dedicated task | **Deferred, 16 Oct 2026, to `garbati-platform` T-015 — resolved 18 Oct 2026, decisions #30–#35 below.** See Section 8. |
| 29 | Idempotency on customer creation | None; client key + request hash | **Confirmed, 16 Oct 2026: client-supplied `idempotencyKey` + request hash, mirroring LedgerApi decision #43.** `POST /v1/customers` requires an `idempotencyKey`, scoped to the calling client; a retry with the same key and a matching hash of the other fields returns the originally-created customer; the same key with a different hash is rejected (409). OrchestratorApi's own design (`Garbati.OrchestratorApi/docs/DESIGN.md` Section 1.4) already assumed this guarantee exists — it derives `orch:{operationId}:create-customer` as a retry-safe key for calling this endpoint. **Numbering note (corrected 17 Oct 2026):** this document's body prose (Sections 1–7) previously cited decision numbers that didn't match this table — a pre-existing authoring error from when this table was first written. Every body citation has been corrected to point at the table row it actually describes. |
| 30 | Reversal-authorization caller | Build dual-control inside IdentityApi (mirroring T-018's joint-approval workflow); trust a single restricted ops-channel caller | **Confirmed, 18 Oct 2026: trust a single ops-channel caller**, gated by new scope `identity.ops.reversal`, never granted to a customer-facing channel. Maker-checker for reversals happens inside the ops channel's own tooling, which does not exist yet and is not designed here. See 8.1. |
| 31 | Does a reversal need the account's own mandate/joint-approval consent? | Inherit the account's `RequiredApprovals`; a lower bar (e.g. any one holder); ops override, bypasses the mandate model entirely | **Confirmed, 18 Oct 2026: ops override.** `research/reversal-authority-patterns.md` Q2 found no standard source answering this — a genuine gap, resolved here by the owner's choice rather than external precedent. See 8.2. |
| 32 | Reversal reason codes | Invent GARBATI-specific codes; reuse ISO 20022/card-network vocabulary | **Confirmed, 18 Oct 2026: `DUPL`/`TECH`/`FRAD`/`CUST`/`UNAUTH`**, reusing converged industry categories (`research/reversal-authority-patterns.md` Q3) plus one GARBATI-specific addition (`UNAUTH`) where no ISO code maps to a consumer claim. `DISPUTE` (card-network-style consumer dispute) deliberately deferred until a real financing/merchant product exists. See 8.4. |
| 33 | Reversal grant shape | A reversal-specific token/grant shape; reuse the existing grant machinery with a new `operation` value | **Confirmed, 18 Oct 2026: reuse**, unchanged `GrantSigner`/claim/expiry/signing logic, `operation: "Reversal"` plus the reason code as an added claim. See 8.5. |
| 34 | Reversal outcome / write-off tracking | No tracking in IdentityApi (leave fully to ops tooling); track a distinct write-off event | **Confirmed, 18 Oct 2026: track it** — new `ReversalOutcomes` entity (`AuthorizationDecisionId`, `Outcome` `Completed`/`WriteOff`, `RecordedAt`, `RecordedBy`, `Notes`), a thin audit record of the recovery decision's conclusion, not the recovery process itself. The owner chose this over the research's more conservative "fully out of scope" recommendation. See 8.6. |
| 35 | Reversal response-time enforcement | Reject a reversal request past some age limit; no code enforcement | **Confirmed, 18 Oct 2026: no code enforcement.** Timestamps are recorded exactly as for any other authorization decision; any real SLA is an ops-process target measured against them, not an IdentityApi rule. See 8.7. |
| 36 | Persisting ReasonCode on AuthorizationDecision | Leave it only in the signed grant (never persisted, decision #13) and the audit Details string; add a dedicated column | **Confirmed, 18 Oct 2026 (T-020 build, #TBD-31): a dedicated column.** Without it, a reversal's reason code becomes unrecoverable the moment its 30-second grant expires — a worse trade than one small, low-risk, additive schema change, given Section 6.3 already lists this as something that must be auditable. |

---

## 10. Open questions and next steps

Resolve these before production use; some can remain implementation tasks for the first build iteration.

1. **The ops channel itself (Section 8.8).** Reversal authorization (Section 8) assumes a trusted ops channel exists, with its own maker-checker process. That channel is not designed and does not exist — it is the next real dependency for reversal to be usable in practice, not just authorizable in principle.
2. **The actual reversal-recovery process (Section 8.8).** `ReversalOutcomes` (8.6) only records that a recovery decision concluded, not how — demand notices, watch-listing, legal/criminal referral, and the point at which a recoverable claim becomes a formal write-off are all undesigned operations/compliance work.
3. **Financial limits.** Which component authoritatively enforces per-transaction and aggregate/daily limits? Any concurrent aggregate limits must be enforced with appropriate atomicity.
4. **KYC provider and Nigerian tier limits.** Which provider or manual workflow supplies verification results? The actual tier evidence/limits still need compliance confirmation (`research/nigerian-regulatory-requirements.md`).
5. **Grant contract finalization.** Canonical amount encoding, operation names, token type/version, clock-skew policy and error codes — ready to implement per Section 3, but not yet exercised against real signing keys.
6. **Channel identity mapping.** Define how authenticated channel identities map to the internal `CustomerId` so callers cannot choose another customer's identity by editing request fields. This is a textbook **BOLA** (Broken Object Level Authorization) risk — OWASP's #1-ranked API security risk — and is correctly flagged as open, not solved, per `research/identityapi-design-review.md` Q5.
7. **Performance.** Set and test latency targets, timeout budgets and bounded retry policies, including validating the 30-second grant window against real measured latency before finalizing it (`research/identityapi-design-review.md` Q1).
8. **Retention and privacy.** Approve encryption, access controls, retention, correction and deletion rules for identity information and verification evidence.
9. **Deceased joint holder's share.** 2.1 explicitly does not auto-freeze a joint account when only one of several holders dies — estate/succession handling for that holder's share is an operations/legal process not designed here.
10. **Sharia confirmation on joint-account equal ownership (AAOIFI).** AAOIFI's Sharia standards describe a joint account as equally owned by its holders by default unless they agree otherwise. Whether a disproportionate approval scheme (e.g. one holder able to act alone on an account with unequal contributions) is appropriate for a Sharia-compliant GARBATI account is a question for a qualified Sharia scholar/board — not resolved by this document or by `research/joint-account-approval-patterns.md`, which only flagged it.
11. **CBN GSI confirmation on joint-account signatory identification.** CBN's Global Standing Instruction guidelines require identifying and obtaining evidence for all signatories to a joint account, and executing GSI mandates with all signatories' execution. No CBN-specific rule on approval-*threshold mechanics* was found; a compliance officer must confirm before production.
12. **Token passthrough / confused-deputy naming.** The `aud` claim and LedgerApi's audience check (once #47 is built) are the right defense against a grant meant for one consumer being accepted by a different one, should the platform ever run more than one LedgerApi-equivalent or Orchestrator — name this explicitly in that future design rather than relying on `aud` being understood as the full mitigation (`research/identityapi-design-review.md` Q5).

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
- **`#TBD-4` (`CustomerIdentityData` fields) — resolved by T-044 (decision #26, `garbati-platform` D-26,
  23 Oct 2026).** Kept to `FullName`/`Email`/`PhoneNumber` only — no BVN/NIN column. Section 6.2's "never
  log raw BVN/NIN" rule implies the same care is needed for *storage* (encryption at rest), which has not
  been decided yet; rather than guess at cryptography, that column is left out entirely pending a
  follow-up decision (likely needed by T-017's KYC evidence work). **Resolution:** store unencrypted for
  now, revisit encryption later — see Appendix H for the `Bvn`/`Nin` columns this unblocked.
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

---

## Appendix C. T-017 build notes (Mandates, single-holder authorization grant issuance)

This appendix records what the second build task (`garbati-platform` T-017) actually built against this
document, continuing Appendix B's numbering convention (`#TBD-n`). It does not change any ratified
decision above, and it deliberately does **not** build Section 3.8's joint-account approval workflow —
that is `garbati-platform` T-018, not started.

**Built:** `Mandates`, `MandatePermissions`, `MandateApprovalPolicies` (always `RequiredApprovals = 1` —
no logic anywhere branches on any other value), `AuthorizationDecisions` (a resolved-decision record for
`GET /v1/authorization/requests/{id}` — **not** the T-018 `PendingApprovalRequest` entity: a
single-holder request resolves synchronously). Five mandate endpoints and two authorization endpoints
from Section 5.1 (`POST`/`GET /v1/customers/{customerId}/mandates`, `GET /v1/accounts/{ledgerAccountId}/mandates`,
`GET /v1/mandates/{mandateId}`, `POST /v1/mandates/{mandateId}/revoke`, `POST`/`GET /v1/authorization/requests[/{id}]`).
ES256 grant signing (`Signing/GrantSigner.cs`) using `Microsoft.IdentityModel.JsonWebTokens`/`Tokens`, keyed
by `kid`, shaped for a second key to be added later (4.3.1) without a breaking config change. Wired
`CustomerService.CascadeMandateRevocationAndFreezeAsync` (T-016's documented no-op) for real. **Not
built** (by explicit scope, deferred to T-018): the joint-account entities (`PendingApprovalRequests`,
`ApprovalRecords`), `.../requests/{id}/approve`, `/v1/accounts/{id}/approval-policy` and its
change-request endpoint, any `RequiredApprovals > 1` logic, planned key rotation (4.3.1) and the
emergency key-compromise procedure (4.3.2) themselves — only a config shape that does not block adding
them later.

- **`#TBD-6` (service interface naming).** `ITransactionAuthorizationService`, not `IAuthorizationService`
  — the latter collides with ASP.NET Core's own `Microsoft.AspNetCore.Authorization.IAuthorizationService`,
  which this project also references via `[Authorize]` policies. A build-time naming conflict, not a
  design choice; flagging it so the name doesn't look arbitrary.
- **`#TBD-7` (denial response shape).** Section 5.1 says a denial "provides a stable reason code" but
  does not say whether that is a `200` body with `Approved: false` or a non-`2xx` status. T-017 chose
  **422** (`AuthorizationDeniedException`, matching `ARCHITECTURE.md`'s "business-rule failure"
  convention and T-016's existing exception→status-code pattern) over a `200` with a boolean flag, so a
  caller can't accidentally treat a denial response as successful by skipping a field check. The decision
  is still persisted and reachable via `GET /v1/authorization/requests/{id}` either way.
- **`#TBD-8` (`CreditAccountId` required).** Section 3.3 calls it "when applicable," implying it could be
  absent for some operation. T-017 requires it unconditionally on `POST /v1/authorization/requests` —
  nothing in this task's scope (single-holder debit/credit requests) has a case where it's meaningfully
  absent, and leaving it optional without a concrete absent-case to test against felt like guessing.
  Revisit if/when an operation without a destination account is actually designed.
- **`#TBD-9` (no idempotency enforcement on `POST /v1/authorization/requests`).** Per the task file's own
  instruction to raise this as a question rather than silently add one: `IdempotencyKey` is a required,
  format-validated *input* field (it ends up in the signed grant claim) but there is no dedup/uniqueness
  storage for it the way `POST /v1/customers` has (decision #29). Section 5.1 does not ask for one here,
  and each call is a fresh decide-and-sign step, not a record-creation step with duplicate-row risk.
  Flagging for the owner/coordinator to confirm this reading is correct.
- **`#TBD-10` (repeat mandate revoke is idempotent, not 409).** Unlike Customer's `Closed`/`Deceased`
  (T-016's `#TBD-2`, chosen as 409-on-repeat because those are terminal, high-stakes transitions), a
  second `POST /v1/mandates/{mandateId}/revoke` call against an already-revoked mandate returns the
  existing state (200) rather than erroring. Nothing is lost by this: the original actor/reason/timestamp
  are untouched, and Section 2.2 already treats a revoked mandate as retained-not-deleted history.
- **`#TBD-11` (`SigningKeyOptions` requires ≥1 key; `ApiKeyOptions` does not).** `ApiKeyOptionsValidator`
  treats zero configured clients as valid (an unusual but legitimate deployment, e.g. health-check-only).
  Grant issuance cannot function with zero signing keys, so `SigningKeyOptionsValidator` fails
  `ValidateOnStart` if `SigningKeys:Keys` is empty or `ActiveKid` doesn't resolve — fail fast at boot,
  not on the first real authorization request.
- **`#TBD-12` (grant claim set: literal task list, not Section 3.3's full prose list).** Section 3.3
  separately lists "Grant/schema version" as a bound claim, but Section 3.5's own illustrative example
  omits it, and the task file's own enumerated claim list (`iss, aud, typ, jti, sub, kid, operation,
  debitAccountId, creditAccountId, amount, currency, idempotencyKey, mandateId, iat, exp`) also omits it.
  T-017 followed the task file's literal list — no `ver` claim. `GrantSigner` does add a standard `nbf`
  (pinned to `iat`) via `SecurityTokenDescriptor`, which is in neither list — ordinary JWT hygiene, not a
  claim carrying any business meaning; flagging it as the one claim present beyond the literal
  enumeration, in case the owner wants it removed for strict conformance.
- **`#TBD-13` (`kid` claim appears twice).** Per Section 3.5's illustrative example, which shows `kid`
  as a body claim. `GrantSigner` sets it there *and* as the real JOSE header parameter a verifier would
  actually key off (RFC 8725 §3.1) — the header copy is the one that matters for verification; the body
  copy exists only because the example shows it there.
- **`#TBD-14` (account-existence check, Section 2.4).** Exactly the same honesty pattern as T-016's
  cascade stub: `MandateService.AttachMandateAsync` has a clearly commented `TODO` where the narrow,
  read-only LedgerApi account-existence call belongs, and does not fake one — LedgerApi has no code to
  call yet. `LedgerAccountId` is accepted as an opaque, caller-asserted string.
- **`#TBD-15` (dev/test ES256 signing keys committed openly).** Per the task file's explicit instruction:
  a dev-only P-256 keypair is committed in `appsettings.Development.json`, clearly labeled, the same
  transparency T-016 used for dev API key hashes — except here the *private key material itself* must be
  present (there is no "hash" equivalent for a signing key). A separate test-only keypair
  (`tests/.../Fixtures/TestSigningKey.cs`) is used by `ApiFixture`/service tests, kept apart from the dev
  key so test and local-dev configuration never share a key. Neither is used, or usable, outside this
  repository's own dev/test environments.
- **`#TBD-16` (`#TBD-15`'s Production guard — QA follow-up).** The owner's approval of `#TBD-15` was
  conditional: something must refuse to start if the committed dev/test key is ever configured with
  `ASPNETCORE_ENVIRONMENT=Production`. `SigningKeyOptionsValidator` now takes `IHostEnvironment` and, when
  `hostEnvironment.IsProduction()`, fails `ValidateOnStart` if any configured key's **public key
  material** fingerprint-matches the known dev key (`appsettings.Development.json`) or the known test
  key (`tests/.../Fixtures/TestSigningKey.cs`) — see `KnownInsecureSigningKeys`. Matching on the
  fingerprint of the parsed public key (`ECDsa.ExportSubjectPublicKeyInfo()`, hashed), not on the
  configured `kid` string, was the deliberate choice: a relabeled `kid` reusing the same key material
  would defeat a `kid`-based check, since `kid` is just a label a deployer controls, not a property of
  the key itself. `SigningKeyOptionsValidatorTests.cs` proves both directions: it fails startup in
  Production for the dev key, the test key, and the dev key under a different `kid`; it does not block
  Development/Testing with the same keys, and does not block Production with an unrelated, valid key.
- **`#TBD-17` (`GrantSigner` concurrency — QA follow-up).** `GrantSigner` was originally a singleton
  holding one long-lived `ECDsa` instance, signed by every concurrent authorization request through that
  same instance. .NET does not document `ECDsa`'s instance methods as safe for concurrent calls, so this
  was fixed to build a fresh `ECDsa` (imported from the cached PEM text) inside every `Sign()` call
  instead — cheap next to everything else one authorization request already does, and avoids a lock that
  would serialize every concurrent request through one signing operation. This surfaced a real,
  reproduced bug along the way: `Microsoft.IdentityModel`'s `CryptoProviderFactory` caches signature
  providers by default, keyed by (roughly) the security key's `Kid` + algorithm, not by object identity —
  since every call necessarily reuses the same `Kid` (it has to; `kid` is the stable, publishable
  identifier a verifier looks up ahead of time), the default cache handed back a stale provider still
  pointing at a *previous* call's already-disposed `ECDsa`, throwing `ObjectDisposedException` under
  concurrent load. This is exactly why "trust `CryptoProviderFactory`'s caching to make concurrent
  signing safe" (one of the options raised) would have been the wrong call — fixed by disabling the
  cache for `GrantSigner`'s `SigningCredentials` (`CacheSignatureProviders = false` on a dedicated
  factory), which sidesteps the stale-reference risk entirely since there is no cache left to go stale.
  `GrantSignerTests.Sign_FromManyThreadsConcurrently_ProducesOnlyValidDistinctGrants` fires 100 concurrent
  signing calls from the thread pool and verifies every resulting grant independently — this is also
  exactly the test that caught the `CryptoProviderFactory` bug above, not a test written after the fact.
- **Closed/Deceased cascade (decision #14) — T-016's `#TBD-5`, now resolved.** `CascadeMandateRevocation
  AndFreezeAsync` queries every `Active` mandate the customer holds, revokes each in the same
  `SaveChangesAsync` call as the customer's own status change, and queues one `AccountFreezeRequested`
  outbox row per distinct `LedgerAccountId` where no other customer still holds an active mandate. T-016's
  `CloseCustomerAsync_WritesNoOutboxMessage_BecauseMandatesDoNotExistYet` test — written specifically to
  force this task to revisit it — is renamed to
  `CloseCustomerAsync_WritesNoOutboxMessage_WhenTheCustomerHasNoMandates` and still asserts zero rows for
  that one case; new tests cover the sole-holder (one row) and surviving-joint-holder (zero rows) cases.

---

## Appendix D. T-018 build notes (joint-account multi-approval workflow)

This appendix records what the third build task (`garbati-platform` T-018) actually built against
Section 3.8, continuing Appendix B/C's `#TBD-n` numbering convention. It does not change any ratified
decision above.

**Built:** `PendingApprovalRequests` and `ApprovalRecords` (3.8.2), with `Kind` (`Debit`/`ThresholdChange`)
and `Status` (`Pending`/`Approved`/`Expired`/`Cancelled`) check constraints and a unique
`(PendingApprovalRequestId, CustomerId)` index enforced by the database. `POST /v1/authorization/requests`
now branches on `MandateApprovalPolicy.RequiredApprovals` (`JointApprovalService.RequestDebitAsync`);
`RequiredApprovals <= 1` delegates unchanged to T-017's `AuthorizationService.RequestAuthorizationAsync`.
New `POST /v1/authorization/requests/{id}/approve`, `GET /v1/accounts/{ledgerAccountId}/approval-policy`
and `POST /v1/accounts/{ledgerAccountId}/approval-policy/change-requests`. The lazy expiry check (3.8.3)
and both halves of decision #24 (eager void on `MandateService.RevokeMandateAsync`, live re-check at
threshold-completion in `JointApprovalService.ApproveAsync`). One EF migration
(`AddJointApprovalWorkflow`) and a migration test. The `RequiredApprovals == 1` path's existing behavior
and tests (`AuthorizationServiceTests.cs`) are untouched - zero diff, confirmed by `git diff --stat`
against the T-017 merge.

- **`#TBD-18` (where the ThresholdChange request's desired new value lives).** Section 3.8.2's
  `PendingApprovalRequest` field list (`LedgerAccountId`, `RequestedBy`, `Kind`, `Operation`,
  `DebitAccountId`, `CreditAccountId`, `Amount`, `Currency`, `IdempotencyKey`, `RequiredApprovals`,
  `Status`, `CreatedAt`, `ExpiresAt`) has no field for the number a `ThresholdChange` request is actually
  proposing - `RequiredApprovals` itself is forced to the account's current active-holder count (3.8.4),
  a different number. T-018 added `RequestedRequiredApprovals` (nullable, `ThresholdChange`-only) to hold
  it. Without this field the feature described in 3.8.4 cannot be built at all; this is flagged as an
  addition to the schema the design document did not itself specify, not a guess at a ratified decision.
- **`#TBD-19` (POST /v1/authorization/requests' response shape, once it can branch).** Section 5.1 says a
  joint account's response is "the pending request's id and status, not a grant yet" but does not define
  a combined response contract. T-018 kept `AuthorizationResponse` (T-017, unchanged) for the immediate-
  grant case and added `PendingApprovalRequestResponse` for the pending case, with the controller
  returning whichever one actually applies (`DebitRequestResult`, a discriminated union) rather than one
  response type with nullable fields standing in for "no grant yet" vs. "something went wrong."
- **`#TBD-20` (where the one-time grant is returned for a joint-account debit).** Nothing in 3.8 says
  which HTTP response actually carries the grant once the threshold is met - by construction it can only
  be the response to the exact `.../approve` call that completes the threshold (decision #13 forbids
  persisting/re-serving it, so there is no other channel to deliver it through). T-018 added a nullable
  `Grant` field to `PendingApprovalRequestResponse`, populated only on that one response; `GET
  .../requests/{id}` never populates it, even after the request is `Approved` (tested:
  `GetPendingRequestAsync_NeverReturnsTheGrant_EvenAfterTheRequestIsApproved`).
- **`#TBD-21` (approval-policy endpoints' scopes).** Section 5.2 enumerates scopes for every T-016/T-017
  endpoint and explicitly says `identity.authorization.check` covers both `POST /v1/authorization/requests`
  and `.../approve`, but says nothing about the two new `.../approval-policy[/change-requests]` endpoints
  this task adds. T-018 chose `identity.mandate.read` for the `GET` (the same concern as the existing `GET
  .../mandates`) and `identity.authorization.check` for the `POST` (it opens a `PendingApprovalRequest`,
  "the same customer-facing authorization concern" Section 5.2 already uses to justify that scope).
  Flagging for the owner/coordinator to confirm or correct.
- **`#TBD-22` (a Debit request's REQUESTER becoming ineligible between creation and threshold
  completion).** Section 3.8.2's `Status` enum (`Pending`/`Approved`/`Expired`/`Cancelled`) has no
  "Denied" state, and 3.8.5/decision #24 only describes re-checking contributing *approvers'* mandates at
  completion time - it does not say what happens if the fresh re-run of the requester's own eligibility
  check (3.8.1 phase 2, "the ordinary single-holder authorization check... using the requester's own
  eligibility as the basis") fails (e.g. their mandate was revoked, or their KYC tier no longer covers the
  operation) while other holders were still approving. T-018 records the denial for audit/traceability
  (the same `AuthorizationDecision` mechanism the single-holder path already uses) and leaves the request
  `Pending` - the same observable outcome decision #24 specifies for a stale *approver*, applied here to a
  stale *requester*. The approving holder whose call triggered the completion attempt still gets back a
  normal `200 Pending` response, since their own approval was validly recorded and nothing about their
  call failed.
- **`#TBD-23` (the Closed/Deceased cascade does not get the eager-void treatment).** The task file scopes
  decision #24's eager half narrowly: "`MandateService`'s revoke path ... must now also [void outstanding
  approvals]." T-018 built exactly that, in `MandateService.RevokeMandateAsync`. `CustomerService.
  CascadeMandateRevocationAndFreezeAsync` (T-016/T-017's `Closed`/`Deceased` cascade, 2.1) also revokes
  mandates directly, through a different code path this task does not touch, and does **not** perform the
  same voiding. A customer becoming `Closed`/`Deceased` with an outstanding approval on some other joint
  account's `PendingApprovalRequest` would leave that approval un-voided by the eager mechanism (the live
  re-check at threshold-completion, 3.8.5, would still catch it defensively if that request ever reaches
  its threshold - so this is a gap in the eager half only, not a hole in decision #24 overall). Flagged
  rather than silently expanding this task's scope to a method the task file does not name.
- **`#TBD-24` (ThresholdChange request creation requires the requester to already be a holder).** Section
  3.8.4 says approving a `ThresholdChange` request needs "every active holder['s]" consent and forces
  `RequiredApprovals` to the active-holder count, but does not explicitly say the *requester* must
  themselves be a current holder at creation time. T-018 added that check
  (`JointApprovalService.RequestThresholdChangeAsync`, `JointApprovalDeniedException` with reason
  `NoActiveMandate`): without it, `RequiredApprovals` could be forced to N while the requester's own
  automatic first approval (decision #21's pattern, applied here too) comes from someone who isn't one of
  the N holders, meaning fewer than every real holder could still reach the threshold - defeating decision
  #22's whole point.
- **`#TBD-25` (live re-check also voids what it finds, not just refuses to complete).** 3.8.5 says a stale
  approval found at the live re-check means "the request's Status stays Pending ... and waits for a
  replacement approval" but does not explicitly say whether the stale `ApprovalRecord` itself gets voided
  at that moment or simply left alone (still counted as "given," just not re-verified again next time).
  T-018 voids it immediately when the live re-check finds it stale, consistent with decision #24's own
  "void immediately" language for the eager half, applied here to the moment the live check discovers
  staleness rather than only to the moment a revoke call is made.
- **`#TBD-26` (new exception-to-status-code mappings).** `PendingApprovalRequestNotFoundException`/
  `ApprovalPolicyNotFoundException` → 404; `PendingApprovalRequestNotPendingException`/
  `DuplicateApprovalException` → 409; `InvalidApprovalThresholdException` → 400 (the task file's own
  words: "reject otherwise with a clear 400" - this is the one business-rule check in this task mapped to
  400 rather than 422, because it genuinely is a request-shape bound, just one that needs a database read
  the stateless validator cannot perform); `JointApprovalDeniedException` → 422, reusing
  `AuthorizationDenialReasons`' existing reason codes rather than inventing new ones for the same
  underlying facts (no active mandate, operation not permitted).
- **Shared code, not duplicated (task file: "share code where natural").** `MandateEligibility.CheckAsync`
  is T-017's customer/KYC/mandate eligibility logic, extracted byte-for-byte (same reason codes, same
  order) into a static helper so `AuthorizationService.RequestAuthorizationAsync`'s constructor and every
  existing call site - including every test in `AuthorizationServiceTests.cs` - needed no change.
  `IssueApprovedGrantAsync`/`RecordDenialAsync` (new, additive methods on
  `ITransactionAuthorizationService`) are the sign-and-persist / deny-and-persist halves of that same
  method, reused by `JointApprovalService` once a request's threshold is met instead of being
  re-implemented.

---

## Appendix E. T-020 build notes (reversal authorization)

This appendix records what the fourth build task (`garbati-platform` T-020) actually built against
Section 8 (decisions #30-#35), continuing Appendix B/C/D's `#TBD-n` numbering convention. It does not
change any ratified decision above, and it deliberately does **not** build any `PendingApprovalRequest`-
style workflow for reversals, any lien/auto-sweep mechanism, or a third `ReversalOutcomes` state - all
three are explicitly out of scope per the task file and decision #30/#34 (reconfirmed by T-021).

**Built:** scope `identity.ops.reversal` (`IdentityScopes`), wired into the dev-only `dev-ops` seed client
(`appsettings.Development.json`) and nowhere else. `Operation = "Reversal"` on the existing
`POST /v1/authorization/requests` contract, with a required `ReasonCode` validated against exactly
`DUPL`/`TECH`/`FRAD`/`CUST`/`UNAUTH` (`ReversalReasonCode`, `AuthorizationRequestValidator`). A new,
deliberately small `ReversalAuthorizationService` (not a variant of `JointApprovalService` or a reuse of
`AuthorizationService.RequestAuthorizationAsync`/`IssueApprovedGrantAsync`) that signs the grant through the
exact same `IGrantSigner`/`GrantClaims` shape and persists an ordinary `AuthorizationDecision` - with zero
mandate/customer/KYC lookups of any kind, proven by three tests that put the same reversal request through
a zero-mandate, single-holder, and 2-of-3 joint account and get the identical outcome from all three. New
`ReversalOutcomes` table/entity (`Completed`/`WriteOff`, nothing else) and
`POST /v1/authorization/requests/{id}/reversal-outcome`, scoped to `identity.ops.reversal`. One EF
migration (`AddReversalAuthorization`) and migration/check-constraint tests mirroring prior tasks' style.

- **`#TBD-27` (the original transaction/chain reference's field, Section 8.3).** The task file calls for
  "a reference to the original transaction/chain being reversed, replacing debit/credit accounts for this
  operation type" but does not define a new field for it, and decision #33 says the grant's claim shape
  does not grow beyond the reason code. T-020 did not add a dedicated field: `DebitAccountId` carries that
  single reference for a `Reversal` request (both in the request contract and in the resulting grant's
  `debitAccountId` claim), and `CreditAccountId` - which has no meaning for a reversal - became nullable on
  `AuthorizationRequest` (T-017's own `#TBD-8` had already flagged exactly this as the case that would
  justify relaxing it: "revisit if/when an operation without a destination account is actually designed").
  When the caller omits it, the grant's `creditAccountId` claim falls back to the same reference rather
  than a required grant field going null. Flagged for the owner/coordinator to confirm this reading of
  "replacing" is correct, rather than a new, dedicated reference field being added instead.
- **`#TBD-28` (scope gating for `POST /v1/authorization/requests`, decision #30).** Every other endpoint
  in this codebase gates on exactly one scope via a static `[Authorize(Policy = ...)]` attribute, checked
  by `ControllerAuthorizationTests.EveryControllerAction_RequiresAKnownScopePolicy`. A `Reversal` request
  needs `identity.ops.reversal` specifically - not `identity.authorization.check` - and the real `dev-ops`
  seed client intentionally holds the former but not the latter (matching decision #30's "a single
  restricted ops caller," not a caller that also happens to have ordinary transfer authority). Since
  `[Authorize(Policy=...)]` is evaluated before the request body is even bound, a single static attribute
  cannot choose between two scopes based on `Operation`'s value. `AuthorizationController.
  RequestAuthorization` now checks the right scope explicitly (`User.HasClaim`, after the validator has
  already confirmed `Operation` is well-formed) and returns `Forbid()` - which still produces the exact
  same `ProblemDetails` 403 shape as a failed `[Authorize]`, since `ControllerBase.Forbid()` invokes the
  same `ApiKeyAuthenticationHandler.HandleForbiddenAsync`. `ControllerAuthorizationTests` now carries a
  single, named, commented exception for this one action, plus a new test proving it still isn't
  anonymous. This is the one structural change this task made to an existing invariant test; flagged
  explicitly rather than silently loosening it.
- **`#TBD-29` (`GrantClaims.MandateId`/`ReasonCode`, decision #31/#33).** `MandateId` became nullable - a
  reversal has no mandate behind it at all (decision #31), and leaving it an empty string or placeholder
  would be a value a verifier could mistake for a real (if odd) mandate id. `GrantSigner` now omits the
  `mandateId` claim from the token entirely when null, rather than encoding a null/placeholder value, so
  every pre-T-020 grant's claim set is byte-for-byte unchanged (verified: `GrantSignerTests`'s existing
  assertions pass unmodified). `ReasonCode` is a new, symmetric, nullable trailing claim added the same
  way for the opposite case (present only for `Reversal`).
- **`#TBD-30` (404 vs 409 for `.../reversal-outcome`, Section 8.6).** The task file explicitly left this
  as the builder's call. T-020 chose: a decision id that does not exist at all → 404
  (`AuthorizationDecisionNotFoundException`, a new exception - no existing one in this codebase covers
  "no such `AuthorizationDecision`"). A decision that exists but was `Denied`, or whose `Operation` was not
  `"Reversal"` → 409 (`ReversalOutcomeNotEligibleException`), matching the same "resource exists but wrong
  state for this action" convention `PendingApprovalRequestNotPendingException` already established. A
  second outcome on an already-recorded decision → 409 (`DuplicateReversalOutcomeException`), per the task
  file's own instruction, matching `Mandates`/`Customer`'s `Closed`-style "terminal state" convention.
- **`#TBD-31` — resolved by decision #36 (Section 9).** Decision #33 only requires the reason code as a
  *grant claim* - it does not say to persist it on the `AuthorizationDecision` row too. T-020 added the
  column anyway and flagged it as an open judgment call rather than a silent addition: the grant itself is
  never logged or persisted (decision #13), so without this column the reason code would become
  permanently unrecoverable the moment the 30-second grant expires, which seemed like the wrong trade for a
  field Section 6.3 lists among what gets audited. The owner has since confirmed a dedicated column is
  correct (decision #36) over the alternative of leaving it only in the grant and the audit `Details`
  string. Low-risk, additive, nullable for every non-`Reversal` decision.
- **`#TBD-32` (one audit event, not two, for a granted reversal, Section 6.3).** Section 6.3 lists "a
  reversal authorization requested, the resulting grant issued or the denial" as things to audit, which
  could read as two separate events per reversal. T-020 records exactly one (`AuditActions.
  AuthorizationApproved`, the same action every other granted decision uses) per granted reversal, matching
  the established one-event-per-decision pattern `AuthorizationService.PersistDecisionAsync` already uses
  for every other operation - there is no separate request/decide phase to split apart here (decision
  #30/#31's whole point: `ReversalAuthorizationService.RequestReversalAsync` is a single synchronous call
  with no denial path once it is reached, unlike the T-018 `PendingApprovalRequest` lifecycle this
  design document's own language echoes). `ReversalOutcomeRecorded` is the one genuinely new audit action.
- **`ReversalReasonCode` kept out of `MandateOperation` (Section 2.2/8.4).** `Operation = "Reversal"` is
  accepted by `AuthorizationRequestValidator` as a second, separate kind of operation string, not added as
  a member of the `MandateOperation` enum (`View`/`Debit`/`Credit`) - that enum specifically describes what
  a *mandate* can permit, and decision #31 means a reversal never touches a mandate at all. Adding
  `Reversal` to it would have wrongly implied a mandate could someday be granted "Reversal" permission.

## Appendix F. T-031 build notes (pending-requests list endpoint)

This appendix records what `garbati-platform` T-031 actually built against the new Section 3.8.7: a
single, purely additive, read-only `GET /v1/authorization/requests?status=&pageSize=&pageToken=` action on
`AuthorizationController`, listing `PendingApprovalRequest` rows. It does not touch
`RequestAuthorization`, `Approve`, `RecordReversalOutcome` or `GetAuthorizationRequest` - no existing
endpoint, scope, route, or response shape changed.

**Built:** `AuthorizationController.ListPendingRequests` (`[HttpGet]` at the controller's own base route,
`[Authorize(Policy = IdentityScopes.AuthorizationCheck)]` - no new scope). Query-parameter validation
(`status` defaulting to `Pending`; `pageSize` defaulting to 20, clamped to [1, 100]) lives in the
controller, same as every other per-request shape check in this codebase. `IJointApprovalService.
ListPendingRequestsAsync` does the actual query: `WHERE Status = @status`, ordered oldest-first by the
row's internal `Id`, keyset-paged with an opaque base64-of-`Id` cursor. The existing
`PendingApprovalRequestResponse` shape is reused exactly as-is (no second, list-specific shape). A new
generic `PagedResponse<T>` record (`Contracts/Responses/PagedResponse.cs`) is the envelope - checked
`LedgerApi` and `Garbati.OrchestratorApi` first; neither has any list endpoint yet (as of 19 Oct 2026), so
there was no existing platform paging convention to reuse, and this one is written to be reusable by
whichever lists those services add next. A new `InvalidPageTokenException`, mapped to 400 the same way
`InvalidApprovalThresholdException` already is (`ExceptionHandlingMiddleware`).

Tests (`JointApprovalServiceTests`): an empty page when nothing matches; multiple `Pending` requests
returned oldest-first; the status filter actually excludes a request forced into `Expired` and vice
versa; a full paged walk across 5 requests with `pageSize = 2` returns exactly those 5, in order, with no
duplicates or gaps; a malformed `pageToken` throws. Plus the one test the task file called out explicitly,
not just documented: a single-holder account's debit request (which grants immediately via
`AuthorizationDecision` and never creates a `PendingApprovalRequest` row) is confirmed **absent** from the
list, alongside a real joint-approval `Pending` request on a different account that correctly **is**
present - both the positive and the negative case, not just the negative one in isolation.
`ListPendingRequestsHttpTests` (new file, `[Collection("Api")]`) covers the query-parameter validation
(`status`/`pageSize`/`pageToken` 400s) that only exists at the controller layer and so isn't exercised by
calling the service directly - deliberately asserts nothing about response *content*, since the `Api`
fixture's single shared Postgres instance is never reset between HTTP tests in the assembly (unlike the
per-test-reset `Postgres` collection `JointApprovalServiceTests` uses), so another test's
`PendingApprovalRequest` rows may already exist by the time this one runs.

- **`#TBD-33` (ordering: oldest-first vs newest-first, task file item 2/Scope).** The task file explicitly
  leaves this as the builder's call, only requiring it be picked and documented consistently. T-031 chose
  oldest-first: an approvals queue is normally worked in the order requests arrived (the oldest unresolved
  request is the most overdue one), the same intuition a human inbox or support-ticket queue uses. Flagged
  for the owner/coordinator in case a future ops-console design (T-028/T-029) wants newest-first instead
  (e.g. "show me what just came in") - that would be a one-line change to `ListPendingRequestsAsync`'s
  `OrderBy`, not a reshape of the endpoint.
- **`#TBD-34` (no lazy expiry on the list, Section 3.8.7).** `GetPendingRequestAsync` (by id) and `ApproveAsync`
  both run `ExpireIfPastDueAsync` before answering, so a stale request is corrected on touch. The list
  endpoint deliberately does not do this per row - the task file says not to build "anything beyond a
  `WHERE Status = @status` filter," and running expiry across every row of every page would be both extra
  business logic and a read endpoint with a write side effect of unpredictable size. The accepted
  consequence: a `Pending` request already past `ExpiresAt` keeps appearing in this list as `Pending` until
  something else touches it individually. Flagged in case an ops-console queue screen (the endpoint's own
  motivating use case) finds this surprising enough to want a background expiry sweep instead - no such
  sweep exists anywhere in this codebase today (Section 3.8.3 is already lazy-only by design), so adding
  one would be new infrastructure, not a tweak to this task.
- **`PagedResponse<T>` placement.** Added to `Contracts/Responses/`, not a new top-level `Contracts/Paging`
  folder - it is a response shape like any other in that folder, just a generic one. If a second list
  endpoint lands on this service (or another Garbati service copies the shape), this is the file to share
  or mirror.

## Appendix G. T-035 build notes (customer-detail-edit endpoint)

This appendix records what `garbati-platform` T-035 actually built: a single, purely additive
`PATCH /v1/customers/{customerId}/details` action on `CustomersController`, closing the gap found while
scoping `OpsApi`'s T-034 maker-checker workflow (a `CustomerServiceOfficer` stages a name/email/phone
change, a `Supervisor` approves it, `OpsApi` then calls this endpoint to apply it) - until now, `IdentityApi`
had no endpoint that could apply such a change at all. It does not touch `CreateCustomer`,
`RecordKycDecision`, `Suspend`/`Reactivate`/`Close`/`MarkDeceased`, or the mandate endpoints - no existing
endpoint, scope, route, or response shape changed.

**Built:** `UpdateCustomerDetailsRequest(string? FullName, string? Email, string? PhoneNumber)` - a partial
update of `CustomerIdentityData` only (not `Customer`; `CustomerReference` is out of scope per the task
file). `UpdateCustomerDetailsRequestValidator` rejects an all-null body (nothing to update) and otherwise
mirrors `CreateCustomerRequestValidator` field-for-field: `Email` must contain `@` if supplied and
non-blank, and `PhoneNumber` has no format check at all - because creation does not check it either, and
inventing a stricter rule for an update than creation already enforces would be exactly the "different
standard for the same data" the task file warns against. **QA fix:** the original version only rejected a
supplied-but-blank `FullName`, not `Email`/`PhoneNumber` - on a PATCH, `null` ("leave this field alone")
and `""` (actively supplying a value) are different instructions, so letting two of the three fields
silently accept `""` and overwrite the stored value with a blank string was a real, asymmetric gap, not a
stylistic nit. All three fields now reject a supplied-but-blank value the same way.
`CustomerService.UpdateDetailsAsync` loads the tracked customer (`GetTrackedCustomerOrThrowAsync`, the same
404-via-`CustomerNotFoundException` path `RecordKycDecisionAsync` already uses), applies only the supplied
fields, and stages a new `AuditActions.CustomerDetailsUpdated` row - one `"Field old -> new"` entry per
supplied field, joined with `"; "`, the same `Details` shape `RecordKycDecisionAsync` already uses for
`KycStatus`/`KycTier` - committed by the same `SaveChangesAsync` call as the entity update. No idempotency
key: a field-level update has no "retry creates a duplicate" risk (setting the same value twice has no
different effect than once), the same reasoning `KycDecisionRequest`/`CustomerStatusChangeRequest` already
rely on. Reuses `IdentityScopes.CustomerWrite` - no new scope.

Tests: `UpdateCustomerDetailsRequestValidatorTests` (all-null fails; one/two/three fields supplied passes;
blank `FullName` fails; invalid `Email` fails; any non-null `PhoneNumber` passes, mirroring
`CreateCustomerRequestValidatorTests`'s coverage exactly). `CustomerServiceTests`: updating one field leaves
the other two unchanged; updating all three together; the audit row's `Details` names only the supplied
field(s) with correct old/new values; an unknown customer throws `CustomerNotFoundException`.
`CustomersControllerTests`: 200 for a valid request; 400 (and no service call) for an all-null body and for
a blank `FullName`. `AuthorizationTests.Endpoints` gained a new theory row for the existing
401/403/reaches-endpoint scope coverage. New `CustomerDetailsHttpTests` (`[Collection("Api")]`, same shared,
never-reset Postgres instance as `ReversalAuthorizationHttpTests`/`ListPendingRequestsHttpTests`) checks 404
for a nonexistent customer and 400 for an all-null body over real HTTP, deliberately asserting nothing about
response content for the same reason those other files don't.

- **Audit record granularity.** The task file says "old -> new values for whichever fields were actually
  supplied," which this reads as "supplied" rather than "actually different from the current value" - a
  caller that re-sends the same `FullName` still gets a `FullName X -> X` audit line, exactly like
  `SuspendCustomerAsync` does *not* audit a no-op repeat (idempotent status transitions are a different,
  deliberately no-op-aware pattern) but `RecordKycDecisionAsync` always audits whatever decision it was
  given, regardless of whether it changed anything. This endpoint follows the latter precedent: it is a
  plain field-level write, not a state-machine transition, so there is no "idempotent repeat" case to
  special-case the way suspend/reactivate do.
- **`Customer.UpdatedAt` left untouched.** Only `CustomerIdentityData.UpdatedAt` is bumped - no field on
  `Customer` itself changes here (unlike `RecordKycDecisionAsync`, which touches `Customer.KycStatus`/
  `KycTier` directly). Bumping `Customer.UpdatedAt` for a change that happened entirely on a different,
  deliberately separated table (Section 2.1/7.1's identity-data split) would make `GET`'s `updatedAt` claim
  a change to fields it does not even expose (`CustomerResponse`'s redacted view, Section 6.2) - `CustomerResponse`
  already doesn't expose `FullName`/`Email`/`PhoneNumber`, so tracking their last-changed time on the
  `CustomerIdentityData` row they actually live on is strictly more correct than mixing sentinel semantics
  into `Customer.UpdatedAt` here.

---

## Appendix H. T-044 build notes (BVN/NIN storage, narrow KYC-tier evidence enforcement)

This appendix records what `garbati-platform` T-044 actually built: the `Bvn`/`Nin` columns `#TBD-4`
(Appendix B) left out pending decision #26, a new set-once endpoint to submit them, and a real but
deliberately narrow slice of `KycTierPolicy.RequiredEvidence` enforcement in `RecordKycDecisionAsync` -
confirmed, before this task, that `RequiredEvidence` was stored but read nowhere in the codebase at all,
so any caller could grant any tier to any customer regardless of what evidence actually existed. This
task closes the BVN/NIN-cardinality half of that gap specifically.

**Built:**

- `CustomerIdentityData.Bvn`/`.Nin` - nullable `string`, unencrypted (decision #26). Additive migration;
  every existing row gets `NULL` for both, no default invented. Format: exactly 11 ASCII digits
  (`IdentityNumberRule`), checked only when a value is actually supplied.
- `PATCH /v1/customers/{customerId}/identity-numbers` (`SubmitIdentityNumbersRequest(Bvn, Nin)`), scope
  `identity.kyc.manage` (not `identity.customer.write` - this is KYC evidence, not contact-detail
  editing). Set-once per field: `null` stored + a supplied value sets it; the *same* value resubmitted is
  an idempotent no-op (200, no second audit row - a real change is what the audit trail should describe,
  not a restated fact); a *different* value against an already-set field is a 409, with no admin-override
  path in this task. `AuditActions.IdentityNumberSubmitted`'s `Details` names only which field(s) changed
  ("Bvn set"/"Nin set") - the submitted value itself is exactly the kind of thing Section 6.2 forbids
  logging, and that discipline applies to this durable `AuditEvent.Details` text too, not only to Serilog
  request logs. Response stays the unchanged `CustomerResponse` - no `Bvn`/`Nin` property added anywhere.
  **QA fix (PR #7 review):** the set-once check was originally pure application-level read-then-write
  with no database-level guard - two concurrent requests for the same customer with *different* values
  could each read `Bvn`/`Nin` as `null`, each pass the "not yet set" branch, and each successfully save,
  the second silently overwriting the first with no 409 to either caller. Fixed with a
  `SELECT ... FOR UPDATE` row lock on the customer's `customer_identity_data` row for the method's
  duration (same pattern `JointApprovalService.ApproveAsync` already uses for an analogous race) - a real
  database-enforced guard, not an application-level check alone. A plain unique constraint (the mandate-
  uniqueness pattern, Section 7.4/decision #19) does not apply here: there is no cross-row uniqueness
  invariant, only a single row's null-to-value transition that two transactions must not race. Proven by
  a genuine concurrency test (`SubmitIdentityNumbersAsync_TwoConcurrentRequestsWithDifferentBvnValues_OnlyOneWins`),
  not two sequential awaited calls.
- `KycEvidenceTokens.AtLeastOneIdentityNumber`/`.BothIdentityNumbers` - two new entries in the existing,
  already-configurable `RequiredEvidence` jsonb vocabulary (decision #11), not a new hardcoded rule
  bypassing it. A new migration (not an edit to the old `SeedPlaceholderKycTierPolicies` migration) adds
  `AtLeastOneIdentityNumber` to the seeded `Tier1`/`Tier2` placeholder rows and `BothIdentityNumbers` to
  `Tier3`, matching `garbati-platform/research/nigerian-regulatory-requirements.md` Q1's real cardinality
  (Tier1/Tier2 need at least one of BVN/NIN; Tier3 needs both) - the rows are still placeholder data
  overall (the `SelfDeclaredIdentity`/`GovernmentId`/`SourceOfFunds` tokens are untouched and still
  unenforced), only this one cardinality fact is asserted as independently confirmed.
- `RecordKycDecisionAsync`, immediately before `customer.KycTier = request.KycTier` (so still only reached
  when `newStatus == KycStatus.Verified`): looks up the currently-effective `KycTierPolicy` for
  `(customer.JurisdictionCode, request.KycTier)` and checks **only** whether
  `AtLeastOneIdentityNumber`/`BothIdentityNumbers` (whichever that policy's `RequiredEvidence` names) is
  satisfied by `CustomerIdentityData.Bvn`/`.Nin`. On failure: 422 (`KycTierEvidenceNotMetException`), and
  the whole decision fails atomically - no `KycVerification` row, no `KycStatus`/`KycTier` change - the
  same "whole operation fails together" shape already used elsewhere (e.g. `AccountOpeningService`'s step
  failures, or a failed chain reversal in LedgerApi). No matching policy row at all is treated as nothing
  to check (the same behavior as before this task existed, not a new failure mode). A non-`Verified`
  decision never reaches this check at all, matching the pre-existing `if (newStatus == KycStatus.Verified)`
  guard exactly.

**Deliberately not built (scope boundary, not an oversight):**

- **`SelfDeclaredIdentity`/`GovernmentId`/`SourceOfFunds` enforcement.** Confirmed, unchanged, pre-existing
  gap - no backing data exists anywhere in the system for any of the three (no column, no document-storage
  mechanism), so enforcing them now would make Tier2/Tier3 permanently ungrantable until a much bigger,
  separate piece of work (document evidence storage) is built. This task closes the BVN/NIN-cardinality
  slice of `RequiredEvidence` specifically, not the full vocabulary.
- **Real external BVN/NIN verification** (NIBSS or any third-party KYC provider confirming the number is
  authentic). This task only stores a self-/ops-submitted value and checks its *presence*, never its
  *authenticity*.
- **Encryption at rest, and retention/purge.** Encryption: decided, unencrypted for now (decision #26).
  Retention: still open (`garbati-platform/research/nigerian-regulatory-requirements.md` Q2/Q3, 5 vs. 6
  years unresolved) - no purge job, no deletion logic; storing indefinitely for now is the unchanged status
  quo for every other field on this table too.
- **Any change to `UpdateCustomerDetailsRequest`/`UpdateDetailsAsync`.** BVN/NIN get their own endpoint
  with different (set-once) semantics; T-035's endpoint is untouched.

**Tests:** migration applies cleanly, existing rows get `NULL` for both new columns (`MigrationTests`,
including a raw-SQL-inserted row that never mentions the new columns, proving no default was invented);
format validation for too-short/too-long/non-digit/non-ASCII-digit values
(`SubmitIdentityNumbersRequestValidatorTests`); set-once behavior including a direct database query
proving a rejected 409 attempt left the stored value unchanged, not just that the response code was right
(`CustomerServiceTests`); a genuine concurrency repro where two different Bvn values race for the same
customer via `Task.Run`/`Task.WhenAll` against two independent `DbContext`s, proving exactly one wins and
the stored value is the winner's, not a mix (`SubmitIdentityNumbersAsync_TwoConcurrentRequestsWithDifferentBvnValues_OnlyOneWins`,
added in the PR #7 QA follow-up above); tier-cardinality enforcement for Tier1/Tier2 (at least one) and Tier3 (both),
including that a non-`Verified` decision never triggers the check regardless of what evidence is on file;
a direct query against the actual `AuditEvent` row proving `Details` never contains the literal submitted
value; and a real HTTP round trip (`IdentityNumbersHttpTests`) parsing a live response's JSON to prove it
carries no `bvn`/`nin` key, not relying on `CustomerResponse` having no such property as the only proof.

---

## Appendix I. T-049 build notes (retried-denial idempotency)

This appendix records what `garbati-platform` T-049 (D-16) actually built. D-16 found that
`AuthorizationService.RequestAuthorizationAsync` mints a fresh `Guid.NewGuid()` `DecisionId` and writes a
brand-new `AuthorizationDecision` row on *every* call, with no lookup by `IdempotencyKey` anywhere — a
client retry (lost response, timeout) therefore produces a second, independent decision for what was
really one logical request. The owner's decision (`garbati-platform/TASKS.md` D-16, 28 Oct 2026,
confirming no separate migration sign-off gate was needed beyond that decision itself): dedupe the
*denial* case only; a retried *approval* keeps minting a fresh grant, unchanged.

**Built:**

- New partial unique index `ix_authorization_decisions_requested_by_idempotency_key_denied` on
  `AuthorizationDecision(RequestedBy, IdempotencyKey)`, `WHERE Approved = false`. Purely additive — it
  creates an index only, mutates no existing row. **Why partial, not a plain unique index:** a plain
  unique index on the same two columns would also block the second row a retried *approval* is supposed
  to get under the owner's decision above; the `WHERE Approved = false` filter means Postgres only
  enforces uniqueness among denied decisions, leaving every approved insert — including a retried one —
  completely outside what the index even looks at.
- `AuthorizationService.PersistDecisionAsync`: the existing `SaveChangesAsync` call is now wrapped in a
  try/catch mirroring `CustomerService.CreateCustomerAsync`'s exact shape (attempt the insert directly, no
  separate "check first" read, which would reintroduce the exact race the constraint exists to close).
  On `DbUpdateException` with the new index's Postgres unique-violation (and only when `!approved` — an
  approved insert never hits this index's filter, so that case always falls through to the unchanged
  `return decision;`): detach the staged `AuthorizationDecision` and the staged `AuditEvent` (confirmed
  `IAuditLogger.Record` returns the same tracked entity `CreateCustomerAsync` detaches — both needed
  detaching for the same reason), re-query the existing denied row by `(RequestedBy, IdempotencyKey)`,
  compare the request's own canonical fields (`Operation`, `DebitAccountId`, `CreditAccountId`, `Amount`,
  `Currency` — not computed outputs like `DenialReason`) against it, and either return the existing
  decision (same request) or throw `DuplicateIdempotencyKeyException` (a different request reusing the
  same key, 409).
- `RequestAuthorizationAsync`'s denial branch now throws `AuthorizationDeniedException` using the
  `DecisionId`/`DenialReason` that `PersistDecisionAsync` actually returns, not the `decisionId` it
  generated locally before the call — on a resolved duplicate, that local id was a fresh guid that was
  never actually persisted, so a caller holding it could never successfully `GetDecisionAsync` it. A
  retried denial now surfaces the *original* decision's real identity.
- `RecordDenialAsync` — `JointApprovalService`'s only caller of the denial path — needed **no change**:
  its own return type is `Task`, not `Task<AuthorizationDecision>`, so it already discards the entity
  `PersistDecisionAsync` returns; `JointApprovalService.cs` itself is therefore completely unmodified by
  this task and benefits from the dedup automatically the next time `RecordDenialAsync` is called with a
  repeated key.

**Deliberately not built (scope boundary, confirmed before starting, not an oversight):**

- **`ReversalAuthorizationService.RequestReversalAsync`** — read directly before starting: it has no
  denial branch at all. Per decision #31, the ops channel's own maker-checker already gates a reversal
  request before this method is ever called; every call here unconditionally sets `Approved = true`.
  There is nothing to dedupe on that path, and the file is untouched by this task.
- **A `RequestHash` column**, unlike `CustomerService`'s pattern. Every field needed to detect "same key,
  different body" (`Operation`, `DebitAccountId`, `CreditAccountId`, `Amount`, `Currency`) already exists
  as a plain column on `AuthorizationDecision` — adding a hash column alongside them would be a second,
  redundant way to ask the same question.
- **Any change to the *approved*-retry behavior.** A retry of an approved request still gets a fresh
  `DecisionId` and a fresh grant, exactly as today — see 3.7's regression-test note above. This is the
  decision the task was explicitly scoped around, not a gap.

**Tests (`AuthorizationServiceTests`, `MigrationTests`):** a retried identical denial returns the same
`DecisionId`/`DenialReason` and leaves exactly one row and one `AuthorizationDenied` audit event; the same
key with a different `Amount` throws `DuplicateIdempotencyKeyException` and leaves the original row's
`Amount`/`DecisionId` untouched; a different idempotency key for the same `RequestedBy` persists both
denials independently; an approved request retried with the same key still gets a second row with a fresh
`DecisionId` (the regression test for the easiest way to get this wrong); the migration applies cleanly
and the index is confirmed to actually exist, be unique, and be filtered via `pg_indexes`/`indexdef` (not
just what the C# model claims to have asked for), plus a raw-SQL-insert test proving the database itself
blocks two denied rows for the same `(RequestedBy, IdempotencyKey)` while allowing two approved rows for
that exact same pair. `JointApprovalService`'s existing denial-path tests needed no changes and were
confirmed passing unchanged by running the full suite.
`AuthorizationTests.Endpoints` gained a new theory row for the existing 401/403/reaches-endpoint scope
coverage. Four pre-existing test files (`AuthorizationServiceTests`, `JointApprovalServiceTests`,
`ReversalAuthorizationServiceTests`, and one test in `CustomerServiceTests` itself) that grant a KYC tier
purely as unrelated test setup were updated to submit a throwaway identity number first - a real,
necessary consequence of this task's new enforcement actually working, not a weakened or deleted test.

## Appendix J. T-053 build notes (joint-approval "no edit path" regression test)

This appendix records what `garbati-platform` T-053 (D-29) actually built. External feedback on D-29
flagged a real, well-known class of bug: a checker reviews a pending maker-checker request, something
mutates it before they approve, and the checker unknowingly approves different content than what they
actually reviewed. The coordinator investigated directly against this repo's real code (not assumed) and
confirmed no such race exists today for `PendingApprovalRequest` (3.8.2) — no code path mutates its
canonical transaction fields (`Operation`, `DebitAccountId`, `CreditAccountId`, `Amount`, `Currency`,
`RequestedRequiredApprovals`) after creation; `ApproveAsync` only ever appends an `ApprovalRecord` and, at
threshold, writes `Status`/`DecisionId` (or the separate `MandateApprovalPolicy` row for a
`ThresholdChange` request); `ExpireIfPastDueAsync` only writes `Status`. `PendingApprovalRequest.cs`'s own
existing doc comment already states this is deliberate, a design decision from T-018, not something this
task discovered for the first time — see 3.8.2's own text. T-053 is test-only: it pins that existing
invariant in code so it cannot silently erode later.

**Built:**

- `JointApprovalNoEditRegressionTests` (`tests/.../Authorization/`): a closed-set reflection assertion on
  `IJointApprovalService`'s public method names (`RequestDebitAsync`, `ApproveAsync`,
  `GetPendingRequestAsync`, `GetApprovalPolicyAsync`, `RequestThresholdChangeAsync`,
  `ListPendingRequestsAsync` — confirmed directly against `Services/Interfaces/IJointApprovalService.cs`
  before writing the test, not copied from the task file), plus a theory asserting neither
  `AuthorizationController` nor `AccountsController` carries an `HttpPutAttribute`/`HttpPatchAttribute` on
  any public action. Deliberately a *closed* set, not merely "no method literally named Edit/Update" — any
  new method on the interface, or any new PUT/PATCH action on either controller, fails this test and
  forces a deliberate look at whether the addition reopens the TOCTOU risk, the same way
  `ControllerAuthorizationTests` (Appendix D-adjacent, 5.2) already pins the scope-policy invariant.
- Proved the test actually catches the regression it exists for, not just that it passes today: temporarily
  added a throwaway `EditPendingRequestAsync` method to `IJointApprovalService`/`JointApprovalService` and
  a throwaway `[HttpPut]` action to `AuthorizationController`, confirmed both new assertions failed with a
  clear message naming the offending member, then reverted both throwaway changes (confirmed via `git diff`
  that the working tree matched `main` again before committing the test).
- **Second round, same reviewer, same D-29 thread:** a complementary *behavioral* test,
  `JointApprovalServiceTests.PendingApprovalRequest_CanonicalContentStaysIdentical_AcrossTheWholeThreeOfThreeApprovalFlow`.
  Where `JointApprovalNoEditRegressionTests` proves no edit surface exists structurally, this one actually
  drives a real 3-of-3 create-then-approve flow (reusing this file's existing `CreateCustomerAsync`/
  `AttachMandateAsync`/`SetRequiredApprovalsAsync` fixtures, no new pattern invented) and diffs
  `Operation`/`DebitAccountId`/`CreditAccountId`/`Amount`/`Currency` against a snapshot taken at creation,
  re-fetched independently via `GetPendingRequestAsync` (not trusting `ApproveAsync`'s own return) at two
  points: after the first non-requester approval (still `Pending` — the actual "checker has it open"
  moment mid-collection) and after the final approval that completes the threshold (now `Approved`). This
  would also catch a subtler bug a structural test cannot — e.g. `ApproveAsync` accidentally reassigning a
  canonical field somewhere along the way. Proved it the same way: temporarily added a line reassigning
  `pending.Amount` right before `ApproveAsync`'s first `SaveChangesAsync`, confirmed the new test failed
  with a clear "Strings differ: Expected 12345.67, Actual 999999.99" message at the first checkpoint, then
  reverted it and confirmed `git diff origin/main` for the touched production file was empty before
  committing the real test.

**Deliberately not built (scope boundary, confirmed before starting, not an oversight):**

- **No production code change of any kind.** This task is test-only, per its own task file.
- **No "withdraw my own pending request" endpoint.** The investigation separately noticed
  `PendingApprovalRequestStatus.Cancelled` is defined but never written anywhere — a real but minor
  product gap, out of scope for this task, left for the owner to decide on separately.

**Tests:** `JointApprovalNoEditRegressionTests` (3 cases: the interface closed-set check, and the
controller PUT/PATCH check run as a theory over both controllers), plus
`JointApprovalServiceTests.PendingApprovalRequest_CanonicalContentStaysIdentical_AcrossTheWholeThreeOfThreeApprovalFlow`
(1 case, added in the same PR for the same reviewer's follow-up). Full existing suite re-run and confirmed
unaffected (expected, since no production code changed) — 364/364 passing.
