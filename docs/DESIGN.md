# Garbati Core Banking Platform
## IdentityApi & Mandate Service - Design Document

**Status:** Design draft with confirmed and proposed decisions  
**Last updated:** 15 October 2026  
**Service name:** `IdentityApi`  
**Platform name:** Garbati

## Purpose and decision status

This document turns the original Identity & Mandate Service question skeleton into a working design for Garbati. It records the decisions agreed so far and distinguishes them from recommendations that still need confirmation. The service should be designed around explicit security and domain invariants before implementation begins.

### Existing LedgerApi decisions carried forward

- **Decision #12:** LedgerApi does not store customer identity data such as email, BVN or NIN. That data has different access, retention and regulatory requirements and belongs outside the ledger.
- **Decision #34:** LedgerApi authorizes calling services, not customers. Customer-level authorization must happen before a trusted service asks LedgerApi to move money.
- **Decision #35:** Services authenticate to LedgerApi using API keys and scopes.

### Confirmed architecture decisions from the current design discussion

1. The platform is named **Garbati**, not Mizan.
2. The intended money-movement flow is **Channel -> Orchestrator -> IdentityApi -> Orchestrator -> LedgerApi**.
3. IdentityApi checks customer eligibility and mandate authority, then issues a **cryptographically signed, transaction-bound authorization grant**.
4. The Orchestrator may verify the grant before forwarding it, but LedgerApi must independently verify the grant and compare it with the actual transaction it receives.
5. LedgerApi must **not call IdentityApi directly** for each transaction. It verifies the grant locally using IdentityApi's trusted public key.
6. Authorization grants have a **30-second validity window** in v1, based on the expectation that transaction processing latency will be very short.
7. The design accepts a limited race window in which a mandate may be revoked after a grant is issued but before it is used. Urgent post-execution recovery can use LedgerApi's reversal process, while recognizing that reversal is a compensating transaction and does not erase the original event.

---

## 1. What is this system?

### 1.1 Purpose and callers

IdentityApi owns customer identity records, KYC status and the mandates that connect customers to Garbati ledger accounts. It decides whether a customer is authorized to perform a particular operation on a specific account, under the applicable customer, KYC and mandate rules.

IdentityApi does not hold ledger balances or post financial entries. A trusted Orchestrator coordinates requests between channels, IdentityApi and LedgerApi. LedgerApi remains authoritative for account state, balance rules, idempotency and the financial posting itself.

### 1.2 Scope for v1

| Capability | V1 decision | Notes |
|---|---|---|
| Customer record creation and maintenance | In scope | Customer can exist before being fully verified. |
| KYC status and current tier | In scope | Verification evidence and tier changes must be auditable. Exact policy limits must be confirmed before production. |
| Customer-to-account mandate | In scope | Single-customer mandate model for v1. |
| Authorization check and signed grant issuance | In scope | Grant is specific to the transaction intent and valid for 30 seconds. |
| Customer authentication, passwords and end-user sessions | Out of scope | Owned by the channel/application. IdentityApi receives a trusted customer identity from the authenticated flow; it must not trust an arbitrary customer ID supplied by an untrusted client. |
| Biometric capture or biometric matching | Out of scope | A future provider or workflow may supply a verification result. |
| OCR or raw identity-document storage | Out of scope | Prefer secured external document storage and references. |
| Credit scoring or financing eligibility | Out of scope | Separate product/domain responsibility. |
| Joint accounts, group mandates and co-signatory rules | Out of scope | Do not partially implement these in v1. |
| Compliance suspension, mandate revocation and privileged review | In scope | Restricted to authorized internal services or operators and fully audited. |
| Full external KYC provider integration | Out of scope for initial build | Keep the model extensible for future integrations. |

### 1.3 Actors

- **Customer:** Acts through an approved channel; does not directly call the internal IdentityApi.
- **Channel:** Authenticates the end customer and submits the transaction request.
- **Orchestrator:** Coordinates the flow and passes the transaction and signed authorization grant to LedgerApi. It cannot create or alter a valid IdentityApi grant.
- **IdentityApi:** Checks KYC, customer status and mandate permissions, and signs an authorization grant when the request is approved.
- **LedgerApi:** Verifies the grant, checks financial and account rules, and posts or rejects the transaction.
- **Operations/compliance actor:** Performs restricted KYC review, customer suspension/reactivation and mandate revocation where authorized.
- **Onboarding agent:** May initiate an onboarding workflow but cannot self-approve privileged KYC changes unless a separately authorized policy explicitly permits it.
- **External verification provider:** May later provide KYC results through a restricted integration.

---

## 2. Core entities

### 2.1 Customer

Use an internally generated `CustomerId` as the stable identifier. Do not use BVN, NIN or an account number as the primary key.

Suggested fields:

- `Id` / `CustomerId`
- `CustomerReference` (optional internal reference)
- `CustomerStatus`: `Active`, `Suspended`, `Deceased` or `Closed`
- `KycStatus`: `Pending`, `Verified`, `Rejected` or `Expired`
- `KycTier`: current tier identifier
- `JurisdictionCode`
- `CreatedAt`, `UpdatedAt`

Sensitive identity fields should be separated from ordinary operational fields and protected by encryption, least-privilege access and an explicit retention policy. The examples above are proposed domain values, not a complete legal KYC standard.

A customer **may exist with zero mandates**. Registration does not by itself grant permission to operate a ledger account. An unverified or otherwise ineligible customer must not receive an authorization grant for an operation that requires a higher KYC status/tier.

### 2.2 Mandate

A mandate links a customer to an existing Garbati ledger account and specifies the operations authorized by that relationship.

Suggested `Mandates` fields:

- `Id`
- `CustomerId`
- `LedgerAccountId` (a cross-service identifier, not a cross-database foreign key)
- `Status` (`Active`, `Revoked`, optionally `Expired` if expiry is a real requirement)
- `CreatedAt`, `CreatedBy`
- `RevokedAt`, `RevokedBy`, `RevocationReason`

Suggested `MandatePermissions` fields:

- `MandateId`
- `Operation` (`View`, `Debit`, `Credit` as applicable)

**V1 rule:** one active customer mandate per ledger account. One customer may have multiple mandates. Joint/group accounts and co-signatory rules are out of scope. A mandate cannot become active until the referenced ledger account's existence has been confirmed.

A revoked mandate is retained for history; it is not deleted or silently reactivated. Restoring access should create a new mandate with a new identifier.

### 2.3 KYC verification and tier policy

Store the customer's current tier on the customer record, with verification attempts/results recorded separately. Use configurable tier-policy data rather than spreading country-specific limits through application code.

Suggested `KycVerifications` fields:

- `Id`, `CustomerId`
- `VerificationType`, `Provider`, `ProviderReference`
- `Status`, `ResultCode`, `VerifiedAt`, `ExpiresAt`
- Minimal metadata required for audit and operational follow-up

Suggested `KycTierPolicies` fields:

- `JurisdictionCode`, `TierCode`, `EffectiveFrom`, `EffectiveTo`
- Required verification evidence and eligible operations
- Applicable transaction rules/limits, where those rules are confirmed and assigned an authoritative enforcement point

Start with a Nigeria-oriented policy profile if that is the first deployment, but keep policy definitions configurable. Do not hardcode regulatory thresholds throughout IdentityApi. Confirm the actual requirements with compliance before production use.

### 2.4 Account creation and mandate attachment

A customer may be created before a ledger account exists. A mandate may not be activated against an unconfirmed account reference.

Recommended onboarding order:

1. Register the customer in IdentityApi.
2. Complete required KYC checks and record the result.
3. Create the ledger account through the trusted Garbati workflow when the customer is eligible.
4. Confirm the ledger account exists, then create/activate the mandate.

If account creation succeeds but mandate creation fails, the account remains without an active customer mandate. Retrying mandate creation must not create a duplicate ledger account. IdentityApi may validate an account reference through a narrowly scoped LedgerApi read operation during mandate creation; LedgerApi does not call IdentityApi in the transaction path.

### 2.5 Customer status and ledger account status

These are different controls and must not be conflated:

- `CustomerStatus` says whether the customer is permitted to operate through IdentityApi.
- `KycStatus`/`KycTier` says whether the required verification policy is satisfied.
- LedgerApi account status says whether the account itself can be used under ledger rules.

A suspended customer receives no new authorization grants. If suspension should freeze every linked account, IdentityApi or the Orchestrator must initiate a durable freeze workflow to LedgerApi, track acknowledgements and retry failures. Do not mark a freeze as complete just because a request was queued. LedgerApi must still enforce its own account freeze/status rules.

---

## 3. Mandate rules and transaction authorization

### 3.1 Domain invariant

> A customer-originated operation must never be authorized unless the customer is eligible to operate, an active mandate permits the requested operation on the exact ledger account, and the request satisfies the applicable authorization policy. If a valid decision cannot be established, no authorization grant is issued and the transaction must not proceed to LedgerApi.

Supporting invariants:

- The signed grant must refer to the exact transaction approved by IdentityApi.
- The received transaction must match all financially material fields in the grant.
- A grant must not authorize a different transaction, account, amount, currency or operation.
- A mandate cannot reference an unconfirmed customer or ledger account.
- Revocation and privileged status changes must be auditable and must not silently delete history.
- Failure to obtain an authorization decision must never be interpreted as approval.

### 3.2 Confirmed transaction flow

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
       - independently verify IdentityApi's signature and grant claims
       - compare the received transaction with the approved transaction details
       - enforce ledger/account/balance/idempotency rules
       - post atomically or reject
```

**LedgerApi must not make a synchronous call to IdentityApi to validate each grant.** It verifies the grant locally using a trusted public key. This keeps IdentityApi out of LedgerApi's per-transaction network dependency while retaining independent verification of IdentityApi's decision.

### 3.3 Signed transaction authorization grant

IdentityApi issues a cryptographically signed grant only after a successful authorization check. Use a standard signed token format (for example, JWS/JWT with an explicitly configured asymmetric algorithm). IdentityApi retains the private signing key. The Orchestrator and LedgerApi receive only the public verification key(s); neither can mint a valid grant.

The grant must bind, at minimum, the following information:

- Issuer identifying IdentityApi (`iss`)
- Intended recipient LedgerApi (`aud`)
- Unique grant/decision identifier (`jti` or equivalent)
- Customer identifier (`sub` or an explicit customer claim)
- Operation type
- Debit/source account identifier
- Credit/destination account identifier, when applicable
- Exact amount and currency in a canonical representation
- Ledger request idempotency key or immutable transaction-intent identifier
- Mandate identifier used for the decision
- Issued-at time (`iat`) and expiry (`exp`)
- Grant/schema version if needed for safe evolution

Do not include BVN, NIN, raw identity data or other unnecessary sensitive data in a signed grant. Signed tokens normally provide integrity and authenticity, not confidentiality; their claims may be readable by a holder.

**Canonical transaction binding is mandatory.** Amounts and other values must use a consistent representation so a logically identical amount cannot be interpreted differently due to formatting or culture. If fees, exchange rates, beneficiaries or other values affect what IdentityApi approves, include them in the signed claims or bind them to an immutable transaction intent.

### 3.4 Verification responsibilities

**Orchestrator:** may verify the grant signature, issuer, expiry and transaction binding before forwarding. It must not modify the transaction after approval. Its verification is an early defence, not a replacement for LedgerApi's checks.

**LedgerApi:** is the final verifier at the point of money movement. It independently checks:

1. The Orchestrator is authenticated and has the required LedgerApi scope.
2. The grant has a valid signature from a trusted IdentityApi key.
3. The issuer, audience, token type/version, issue time and expiry are acceptable.
4. The signed operation, accounts, amount, currency and idempotency/intent identifier match the actual incoming request.
5. The grant has not been used to authorize a different transaction.
6. The actual operation passes LedgerApi's account-status, balance, idempotency and double-entry rules.

On success, LedgerApi stores the grant/decision identifier with the transaction and relevant audit record. This provides a durable link between the authorization decision and the financial result. A signature-valid grant does not override LedgerApi's independent financial rules.

**Two sources of truth:** IdentityApi is authoritative for customer identity, KYC and what the mandate authorizes; LedgerApi is authoritative for account/ledger state and whether money was actually posted. The Orchestrator owns neither truth. The signed grant is the tamper-evident link between IdentityApi's authorization decision and the transaction LedgerApi receives.

### 3.5 Example grant (illustrative claims, not a token to copy into production)

```json
{
  "iss": "Garbati.IdentityApi",
  "aud": "Garbati.LedgerApi",
  "typ": "transaction-authorization",
  "jti": "unique-grant-id",
  "sub": "customer-id",
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

This is an example of the signed claims only. A production token must use a vetted token library, a fixed allowed signing algorithm, protected key management and validation rules. The unique grant ID should be persisted as the authorization decision reference in the ledger transaction/audit trail rather than generated and then left unused.

### 3.6 Mandate revocation and the 30-second window

**Confirmed v1 policy:** authorization grants expire 30 seconds after issuance. The assumption is that Garbati's transaction path should have very low latency and an approved grant should normally be delivered to LedgerApi almost immediately. A short lifetime reduces the period in which an outstanding authorization remains usable.

The design explicitly accepts a narrow race: a mandate may be revoked after IdentityApi issues a grant but before LedgerApi consumes it. The 30-second expiry does not guarantee that a revoke will invalidate an already issued grant immediately. New grant requests after revocation must be denied.

If an urgent revocation happens after a transaction has already posted, LedgerApi's reversal process can be initiated promptly where appropriate. Reversal is a new compensating financial transaction; it does not erase the original posting and may not automatically undo external settlement or other downstream effects. It is a recovery measure, not a substitute for checking the grant before posting.

### 3.7 Replay and retries

A valid signature alone does not prevent replay. LedgerApi must bind use of the grant to the exact request and enforce its existing idempotency rules. Repeating the same request with the same idempotency key should return or recover the original result rather than move money again. Reuse of a grant with a changed amount, account, currency, operation or idempotency key must be rejected.

For a grant arriving after expiry, LedgerApi rejects it. The orchestrator/channel may request a fresh authorization if the business flow permits; it must not extend or rewrite the signed grant.

---

## 4. Consistency, performance and availability

### 4.1 Check frequency and caching

Check authorization for each customer-originated money-moving request. The expectation is that mandates change far less frequently than they are checked, but **v1 will not cache positive authorization decisions**. This avoids accepting a stale cached approval after a known revocation. The signed grant itself is the short-lived authorization artifact; its 30-second validity is the explicitly accepted window.

Measure end-to-end latency before optimizing. Any future cache requires an explicit staleness/revocation policy rather than an implicit default.

### 4.2 Service failures

Garbati must fail closed for customer-originated operations that require IdentityApi authorization:

| Result | Behaviour |
|---|---|
| Valid positive decision and grant | Orchestrator forwards the transaction and grant. |
| Explicit denial | Stop; do not call LedgerApi for that operation. |
| IdentityApi timeout/unavailable | Do not proceed to LedgerApi. Return a temporary-service failure, not a fabricated denial. |
| Invalid signature, expired grant or transaction mismatch | LedgerApi rejects the request before posting. |
| LedgerApi timeout after submission | Use the ledger's idempotency/truth-recovery path to determine the outcome; do not blindly issue a new transaction. |

Bounded retries may be used for retryable errors. An inability to establish authorization must never become permission to transact.

### 4.3 Clock and key management

Because grants expire after 30 seconds, the participating services need synchronized clocks and a deliberately chosen, small clock-skew allowance. Do not create a tolerance that silently turns the short lifetime into a much longer one.

Signing keys must be stored securely and rotated using a planned procedure. Include a key identifier where supported so LedgerApi can select the correct trusted public key during rotation. The algorithm must be configured by the application, not selected freely by token input. Define how current and retiring verification keys are trusted before production.

---

## 5. API surface and service authorization

### 5.1 Proposed v1 endpoints

| Endpoint | Purpose |
|---|---|
| `POST /v1/customers` | Create a customer record. |
| `GET /v1/customers/{customerId}` | Retrieve an appropriately redacted customer view for an authorized caller. |
| `PATCH /v1/customers/{customerId}/kyc` | Record an authorized KYC status/tier decision. |
| `POST /v1/customers/{customerId}/mandates` | Attach an existing ledger account to the customer with explicit permissions. |
| `GET /v1/customers/{customerId}/mandates` | List mandates for an authorized caller. |
| `GET /v1/mandates/{mandateId}` | Retrieve mandate status and permitted operations. |
| `POST /v1/mandates/{mandateId}/revoke` | Revoke a mandate and record the actor/reason. |
| `POST /v1/authorization/check` | Check a customer operation and issue a signed grant only when allowed. |
| `POST /v1/customers/{customerId}/suspend` | Suspend a customer through a restricted workflow. |
| `POST /v1/customers/{customerId}/reactivate` | Reactivate only after required review and checks. |

Endpoint names and exact request/response DTOs remain subject to review. A successful authorization response should provide the signed grant and a unique decision/grant reference. A denial should provide a stable reason code without exposing unnecessary sensitive details. An internal error or timeout is not an authorization denial.

### 5.2 Authorization scopes

Proposed IdentityApi scopes:

- `identity.customer.read`
- `identity.customer.write`
- `identity.kyc.manage`
- `identity.mandate.read`
- `identity.mandate.write`
- `identity.authorization.check`
- `identity.ops.freeze`

A channel/payment service must not receive compliance override permissions by default. Internal operations scopes must be separately assigned and audited. The service identity that calls LedgerApi to validate an account during mandate setup should have only the narrow account-read permission required; IdentityApi does not need transaction-transfer permission for that job.

LedgerApi continues to authenticate the Orchestrator as a service using its existing API-key-and-scope mechanism. That authentication proves which service is calling; the signed grant separately proves the integrity and origin of the customer authorization decision.

### 5.3 Which service calls which?

The Orchestrator (or the designated trusted channel/payment service) calls IdentityApi before sending a customer-originated transaction to LedgerApi. LedgerApi does not call IdentityApi in the transaction path. It verifies the grant locally.

IdentityApi may call LedgerApi for a narrow account-existence check during mandate setup, if this is required by the onboarding workflow. This is separate from per-transaction authorization and does not reverse the agreed dependency direction during money movement.

---

## 6. Failure, truth, logging and audit

### 6.1 Authorization endpoint returns an error

If `POST /v1/authorization/check` returns a 500, times out or cannot provide a valid grant, the Orchestrator must not send the money-moving request to LedgerApi. It may perform bounded retries where safe. A positive decision must be explicit and represented by a verifiable grant.

If LedgerApi returns an ambiguous failure after a submission, the Orchestrator must use the LedgerApi idempotency and transaction-status/truth-recovery mechanisms. It must not assume that an HTTP 500 means no money moved.

### 6.2 Logging rules

Never log:

- Raw BVN or NIN values.
- Identity document images, raw document payloads or full OCR text.
- API keys, private signing keys, access tokens or complete authorization-grant tokens.
- Complete request bodies that may contain personal or verification data.
- Unnecessary sensitive identity information.

Prefer structured operational identifiers and outcomes, such as `CustomerId` (opaque but still access-controlled), `MandateId`, `DecisionId`, operation, reason code, outcome, elapsed time and correlation ID. Do not include sensitive fields merely because they are convenient for debugging.

### 6.3 Audit events

Keep durable audit events separate from diagnostic application logs. Audit at least:

- Customer creation and privileged identity-data changes.
- KYC verification outcomes and tier changes.
- Customer suspension/reactivation.
- Mandate creation and revocation.
- Privileged overrides, including actor and reason.
- Authorization decisions needed for investigation and traceability, with the grant/decision ID and outcome.
- Freeze propagation attempts and acknowledgements where applicable.

Audit events should be append-only from ordinary application code, access-restricted and retained according to approved policy. Do not copy BVN/NIN or document contents into audit records. Correlation IDs should connect diagnostics, authorization decisions and ledger outcomes without replacing the unique decision/grant ID.

---

## 7. Data and storage

### 7.1 Suggested tables

| Table | Responsibility |
|---|---|
| `Customers` | Internal customer identity, current operational status, KYC state/tier and timestamps. |
| `CustomerIdentityData` | Sensitive identity/contact fields needed by the service, with stricter access and encryption. |
| `KycVerifications` | Append-only verification attempts/results and provider references. |
| `Mandates` | Customer-to-ledger-account relationship, state, creation and revocation details. |
| `MandatePermissions` | Operations allowed by each mandate. |
| `KycTierPolicies` | Configurable jurisdiction/tier rules with effective dates. |
| `AuditEvents` | Durable history of important security and business actions. |
| `OutboxMessages` | Durable work, such as account-freeze propagation, with retry and completion status. |

Names are suggested table names and can be adjusted to the project's naming conventions.

### 7.2 Identity documents and sensitive data

Do not store raw KYC document images in ordinary relational columns as the default. Prefer references to a secured object store or external verification provider, and store only the outcome and metadata required for authorization. Secure the referenced objects with authentication, least-privilege access, encryption and audited retrieval.

For sensitive values that genuinely must be retained, use appropriate encryption and managed key protection. If exact-match lookup is needed, evaluate a keyed HMAC fingerprint rather than an ordinary unsalted hash, while retaining an encrypted value only if retrieval is necessary. Define retention, correction and deletion rules with the appropriate compliance stakeholders before production.

### 7.3 Immutable versus mutable data

- Customer status and current KYC tier may change through explicit, audited transitions.
- KYC verification attempts/results should be preserved as historical records; a new verification creates a new record rather than overwriting history.
- A mandate may transition from active to revoked; the revocation evidence remains available.
- For v1, changing a mandate's authority should preferably be implemented as revoke-and-create rather than silently rewriting the old permission history.
- Audit events are append-only from normal application code.
- Sensitive personal data must remain correctable/removable where applicable obligations require it; audit integrity does not mean personal data should be retained forever without justification.

### 7.4 Cross-service integrity

IdentityApi and LedgerApi have separate databases. Do not create a database foreign key across services. Use stable identifiers and API-level validation. Enforce within IdentityApi that each mandate references a customer and that v1's one-active-mandate-per-account rule cannot be violated under concurrent requests, ideally with a database constraint as well as application validation.

---

## 8. Decisions log

The status column distinguishes decisions confirmed by the current discussion from recommendations carried forward for review.

| Decision | Options considered | Decision and status/rationale |
|---|---|---|
| **1. Platform name** | Mizan; Garbati | **Confirmed: Garbati.** |
| **2. Service boundary** | Identity data in LedgerApi; separate service | **Carried forward:** separate `IdentityApi`, consistent with Decision #12. |
| **3. Customer authentication** | IdentityApi owns login; channel owns login | **Proposed:** channel owns customer login/session in v1; IdentityApi authorizes customer activity. |
| **4. Customer identifier** | Government ID; account number; internal ID | **Proposed:** internal `CustomerId`; avoid sensitive government identifiers as primary keys. |
| **5. V1 mandate model** | Single customer; joint/group/co-signatory | **Proposed:** one active customer mandate per account; joint/group out of scope. |
| **6. Transaction flow** | Ledger calls IdentityApi; trusted caller obtains grant first | **Confirmed:** Channel -> Orchestrator -> IdentityApi -> Orchestrator -> LedgerApi. LedgerApi does not call IdentityApi directly for transactions. |
| **7. Authorization evidence** | Plain allow/deny response; signed transaction-bound grant | **Confirmed:** asymmetric signed authorization grant so LedgerApi can independently verify the decision. |
| **8. Grant content** | General customer approval; exact transaction binding | **Confirmed in principle:** bind operation, accounts, amount, currency, customer, mandate and idempotency/intent identifier; finalize the claim schema before implementation. |
| **9. Grant expiry** | Longer lifetime; short lifetime | **Confirmed:** 30 seconds from issuance based on expected low latency; keep clock skew small. |
| **10. Revocation race** | Immediate global invalidation; short outstanding-grant window | **Confirmed:** accept the short in-flight window in v1 and deny new grants after revocation. Reversal is recovery, not erasure. |
| **11. Grant verification** | Trust Orchestrator; Ledger calls IdentityApi; Ledger verifies locally | **Confirmed:** Orchestrator may verify, and LedgerApi independently verifies locally; no direct runtime call from LedgerApi to IdentityApi. |
| **12. Caching** | Cache positive authorization; live grant check | **Proposed:** no positive decision caching in v1, to avoid stale approvals. |
| **13. Service outage** | Fail open; fail closed | **Proposed:** fail closed for customer-originated operations; no valid grant means no money-moving request. |
| **14. KYC tiers** | Hardcoded regional rules; configurable policy | **Proposed:** configurable tiers for the first deployment jurisdiction. |
| **15. KYC documents** | Store raw files in SQL; external secure store/reference | **Proposed:** external secured storage/references by default. |
| **16. Audit history** | Ordinary logs only; separate durable audit trail | **Proposed:** separate append-only audit events. |
| **17. Customer suspension** | Change status only; propagate freezes to ledger accounts | **Proposed:** deny new grants immediately and propagate account freezes where required, tracking acknowledgements. |
| **18. Repository name** | `IdentityApi`; `MandateApi` | **Proposed:** `IdentityApi`, because it covers both identity and mandate responsibilities. |

---

## 9. Open questions and next steps

Resolve these before production use; some can remain implementation tasks for the first development iteration.

1. **KYC policy:** What exact evidence and fields are required at registration and at each tier? Have the rules confirmed by the appropriate compliance stakeholders.
2. **Financial limits:** Which component authoritatively enforces per-transaction and aggregate/daily limits? Any concurrent aggregate limits must be enforced with appropriate atomicity, not merely checked once in IdentityApi.
3. **KYC provider:** Which provider or manual workflow supplies verification results? Who may promote a tier, and what review is required?
4. **Grant contract:** Finalize the signed claim schema, canonical amount encoding, operation names, token type/version, key ID, clock-skew policy and error codes.
5. **Grant replay:** Define database persistence and idempotency treatment so the same grant cannot authorize a changed transaction and retries cannot create duplicate postings.
6. **Key lifecycle:** Choose the signing algorithm and key-management mechanism, document rotation/compromise handling, and configure trusted public keys in LedgerApi.
7. **Revocation/freeze:** Confirm that the accepted 30-second outstanding-grant window matches business expectations. Define urgent freeze propagation and whether particular risk events require additional blocking controls.
8. **Reversal:** Define who may initiate a reversal, required reason codes/approvals, the expected operational response time, and how reconciliation handles external effects that a ledger reversal cannot undo by itself.
9. **Channel identity mapping:** Define how authenticated channel identities map to the internal `CustomerId` so callers cannot choose another customer's identity by editing request fields.
10. **Performance:** Set and test latency targets, timeout budgets and bounded retry policies for Orchestrator, IdentityApi and LedgerApi.
11. **Retention and privacy:** Approve encryption, access controls, retention, correction and deletion rules for identity information and verification evidence.
12. **V1 boundaries:** Revisit joint/group mandates only when a concrete requirement exists; do not implement partial co-signatory behaviour by implication.

### Recommended implementation order

1. Define customer, KYC and mandate state transitions and their database constraints.
2. Define the authorization-check request/response DTOs and signed-grant claims.
3. Implement signature issuance in IdentityApi and independent validation in LedgerApi using test signing keys.
4. Add exact transaction-to-grant comparison, expiry checks and ledger-side grant/decision ID persistence.
5. Test tampering of amount, account, currency, operation and idempotency key; expired grants; invalid signatures; key rotation; retries; and mandate revocation inside the 30-second window.
6. Implement customer suspension, mandate revocation, audit events and any required account-freeze outbox workflow.
7. Load-test the full path and validate that observed latency supports the chosen 30-second validity window.

---

## Appendix A. Security acceptance tests

The v1 design should not be considered complete until automated tests prove at least the following:

- A correctly signed, unexpired grant matching the request passes grant validation.
- A grant signed with an untrusted key is rejected.
- Any changed account, amount, currency, operation or idempotency key causes rejection.
- A grant for a different audience or issuer is rejected.
- A grant older than the allowed 30-second lifetime is rejected, accounting only for the deliberately configured small clock-skew allowance.
- A repeated identical request with the same idempotency key does not post money twice.
- Reusing a grant for a different request is rejected.
- A revoked mandate cannot obtain a new positive grant.
- If IdentityApi times out or fails, the Orchestrator does not send an unapproved customer transaction to LedgerApi.
- If LedgerApi rejects the grant, no ledger entries are committed.
- A posted transaction can be traced to its authorization decision ID, correlation ID and ledger transaction reference without logging the grant token or sensitive identity data.
- An urgent reversal creates a compensating transaction and preserves the original posting and audit history.
