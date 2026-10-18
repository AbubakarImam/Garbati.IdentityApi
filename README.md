# Identity API

The customer-identity and KYC service for GARBATI, built with ASP.NET Core 8, EF Core and PostgreSQL.

The design, including every trade-off, is in [docs/DESIGN.md](docs/DESIGN.md) - start there for the *why*. This README covers the *how*. See [`garbati-platform/ARCHITECTURE.md`](https://github.com/AbubakarImam/garbati-platform/blob/main/ARCHITECTURE.md) for how this service fits the rest of the platform: **Channel → Orchestrator → IdentityApi → Orchestrator → LedgerApi**.

## What this build covers

This repository has had **two build tasks** so far. `garbati-platform` T-016 built the project skeleton, `Customer` records and KYC. `garbati-platform` T-017 (this build) adds `Mandates` and single-holder (`RequiredApprovals = 1`) transaction-authorization grant issuance - signed ES256 grants a customer's own mandate is enough to obtain. It deliberately does **not** build Section 3.8's joint-account, multi-holder approval workflow - that is `garbati-platform` T-018, a separate follow-up, not started. See `docs/DESIGN.md` Appendix B (T-016) and Appendix C (T-017) for the full list of build-time judgment calls each task made.

## Core pieces

- **Customer** - internal, generated `CustomerId` (never a BVN, NIN or ledger account number); `CustomerStatus` (`Active`/`Suspended`/`Deceased`/`Closed`) and `KycStatus`/`KycTier`.
- **CustomerIdentityData** - sensitive fields (name, contact) kept in their own table, separate from `Customer`'s operational fields.
- **KycVerification** - an append-only record of each verification attempt and its outcome.
- **KycTierPolicy** - configurable tier rules, not hardcoded. Seeded with **placeholder** Nigerian tiers only (see the migration); the real evidence/limits are still pending compliance confirmation.
- **AuditEvents** / **OutboxMessages** - a durable, append-only audit trail, and durable outbox rows for work (e.g. account freezes) that the Orchestrator will eventually consume. No Orchestrator exists yet, so no message has a consumer today - this is expected at this stage.
- **Mandate** / **MandatePermission** - links one customer to one ledger account with the operations (`View`/`Debit`/`Credit`) they're allowed to request on it. At most one *active* mandate per (customer, account) pair, enforced by a real database partial unique index - but different customers can each hold their own active mandate on the same account (that's a joint account; T-018 builds the multi-approval workflow on top of it, not this task).
- **MandateApprovalPolicy** - one row per ledger account, created alongside its first mandate. Always `RequiredApprovals = 1` in this build; the shape exists so T-018 doesn't need a disruptive migration later.
- **AuthorizationDecision** - a durable record of one resolved authorization decision (approved or denied), so a past decision can be looked up by id. Never stores the signed grant token itself.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (pinned by `global.json`)
- [Docker](https://www.docker.com/) (for the local database and for the tests)

## Getting started

**1. Start PostgreSQL.** The Development connection string expects it on port `5434`:

```bash
docker run -d --name identity-postgres -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=identityapi -p 5434:5432 postgres:16-alpine
```

**2. Apply the migrations.** `dotnet-ef` is pinned in the repo's tool manifest:

```bash
dotnet tool restore
dotnet ef database update --project src/Garbati.IdentityApi
```

**3. Run the API:**

```bash
dotnet run --project src/Garbati.IdentityApi --launch-profile http
```

Swagger UI is at http://localhost:5294/swagger (click **Authorize** and paste a development key from [Authentication](#authentication)).

## Running the tests

```bash
dotnet test
```

Service tests run against a real PostgreSQL container started by [Testcontainers](https://dotnet.testcontainers.org/), so Docker must be running. No local database is needed for tests.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `POST` | `/v1/customers` | Create a customer record (`CustomerStatus = Active`, `KycStatus = Pending`). Requires an `idempotencyKey` - see [Idempotency](#idempotency) |
| `GET` | `/v1/customers/{customerId}` | Redacted customer view - never raw `CustomerIdentityData` fields |
| `PATCH` | `/v1/customers/{customerId}/kyc` | Record a KYC verification attempt and status/tier decision |
| `POST` | `/v1/customers/{customerId}/suspend` | Suspend a customer (idempotent if already suspended) |
| `POST` | `/v1/customers/{customerId}/reactivate` | Reactivate a suspended customer (idempotent if already active) |
| `POST` | `/v1/customers/{customerId}/close` | Close a customer - terminal; a repeat attempt returns 409 |
| `POST` | `/v1/customers/{customerId}/mark-deceased` | Mark a customer deceased - terminal; a repeat attempt returns 409 |
| `POST` | `/v1/customers/{customerId}/mandates` | Attach a mandate to an existing ledger account (and create its `MandateApprovalPolicy` if this is the account's first mandate) |
| `GET` | `/v1/customers/{customerId}/mandates` | List a customer's mandates |
| `GET` | `/v1/accounts/{ledgerAccountId}/mandates` | List every active holder of an account |
| `GET` | `/v1/mandates/{mandateId}` | One mandate's status and permissions |
| `POST` | `/v1/mandates/{mandateId}/revoke` | Revoke a mandate (idempotent if already revoked) - requires a `reason` |
| `POST` | `/v1/authorization/requests` | Request a signed, transaction-bound authorization grant. Resolves synchronously - approved (200, with the grant) or denied (422, with a reason code). Also accepts `operation: "Reversal"` (see [Reversal authorization](#reversal-authorization)) |
| `GET` | `/v1/authorization/requests/{id}` | Look up a past decision by id - never re-serves the grant itself |
| `POST` | `/v1/authorization/requests/{id}/reversal-outcome` | Record a reversal's ultimate recovery outcome (`Completed`/`WriteOff`) - see [Reversal authorization](#reversal-authorization) |

`close` and `mark-deceased` are T-016's own addition - `docs/DESIGN.md` Section 5.1 lists only `suspend`/`reactivate`; see Appendix B for why.

### Status codes

| Status | Meaning |
|---|---|
| `200` | Success |
| `400` | Request failed validation |
| `401` | Missing or unknown API key |
| `403` | The API key lacks the endpoint's scope |
| `404` | Customer, mandate, or authorization decision not found |
| `409` | Customer status transition not allowed (e.g. closing an already-Closed customer), a customer already has an active mandate on the account, an idempotency key reused with a different request, or a reversal outcome request against a decision that isn't an approved Reversal (or already has an outcome) |
| `422` | Authorization request denied - see [Authorization grants](#authorization-grants) |
| `500` | Unexpected error |

### Idempotency

`POST /v1/customers` requires a client-supplied `idempotencyKey` (1-128 characters: letters, digits, `-`, `_`, `.` or `:`), scoped to the calling API client - the same scheme as LedgerApi's `POST /api/accounts` (`docs/DESIGN.md` decision #29, mirroring LedgerApi decision #43). A retry with the same key and the same other fields returns the customer already created instead of a second one; the same key with different fields gets **409**.

`POST /v1/authorization/requests` also takes an `idempotencyKey` (same format), but it is **not** a dedup key here - there is no stored uniqueness check on it. It is required only because it is one of the fields bound into the signed grant. See `docs/DESIGN.md` Appendix C, `#TBD-9`.

### The Closed/Deceased cascade

`docs/DESIGN.md` Section 2.1 (decision #14): becoming `Closed` or `Deceased` automatically revokes every mandate the customer holds, and queues an account freeze (an `OutboxMessages` row) for every account where it was their only active mandate. A joint account with a surviving active holder is **not** frozen - only that customer's own mandate is revoked. T-016 built the status transition and the freeze message's shape without anything to actually look up; this build (T-017) wires the real lookup and revocation.

### Mandates and joint accounts

A mandate links one customer to one ledger account with the operations they can request on it (`View`/`Debit`/`Credit`). At most one *active* mandate per (customer, account) pair - enforced by a database partial unique index, not application logic alone - but different customers can each hold their own active mandate on the same account. That's what makes an account "joint"; this build doesn't exercise that case beyond allowing it to exist (see `docs/DESIGN.md` Appendix A's security-acceptance tests), since the multi-holder *approval* workflow on top of it (Section 3.8) is T-018.

`LedgerAccountId` is never checked against LedgerApi for real yet - LedgerApi has no code to call. It's accepted as an opaque string the caller asserts exists (see `docs/DESIGN.md` Appendix C, `#TBD-14`).

### Authorization grants

`POST /v1/authorization/requests` checks that the requesting customer is `Active`, their KYC tier permits the requested operation, they hold an `Active` mandate on `debitAccountId` permitting that operation, and the account's `MandateApprovalPolicy.RequiredApprovals` is `1` (always true in this build - there's no endpoint yet to make it anything else). On success it returns a signed ES256 grant (claims: `iss`, `aud`, `typ`, `jti`, `sub`, `kid`, `operation`, `debitAccountId`, `creditAccountId`, `amount`, `currency`, `idempotencyKey`, `mandateId`, `iat`, `exp = iat + 30s`), valid for 30 seconds, and a decision id. On denial it returns **422** with a stable reason code (`CustomerNotFound`, `CustomerNotActive`, `CustomerNotVerified`, `OperationNotEligibleForKycTier`, `NoActiveMandate`, `OperationNotPermittedByMandate`, `ApprovalPolicyNotSingleHolder`) - never a grant, never a stack trace.

The grant itself is never logged or persisted anywhere - only its `jti` (the decision id). `GET /v1/authorization/requests/{id}` looks up a past decision's outcome, but never re-serves the grant.

No LedgerApi exists yet to actually verify a grant (decision #47 is deferred, per decision #27/D-6) - nothing here assumes it does.

### Reversal authorization

`docs/DESIGN.md` Section 8 (decisions #30-#35): a reversal request comes only from a trusted internal ops caller (never a customer-facing channel), and deliberately bypasses the account's own mandate/`MandateApprovalPolicy` entirely - the ops channel's own maker-checker process (which does not exist yet) is the real gate, not a second approval-collection system inside IdentityApi.

`POST /v1/authorization/requests` accepts `operation: "Reversal"` with a required `reasonCode` (`DUPL`/`TECH`/`FRAD`/`CUST`/`UNAUTH` - `DISPUTE` is deliberately not supported yet). It is gated by `identity.ops.reversal`, not `identity.authorization.check` - the only endpoint in this service where the same route needs two different, mutually exclusive scopes depending on the request body. On success it returns the same signed grant shape as any other operation, with `operation: "Reversal"` and the reason code as an added claim, and no `mandateId` claim at all (there is no mandate behind a reversal).

`POST /v1/authorization/requests/{id}/reversal-outcome` (scope: `identity.ops.reversal`) records the reversal's ultimate recovery outcome once it's known - `Completed` or `WriteOff`, nothing else. It is a thin audit record, not a collections/lien workflow: `404` if the decision doesn't exist, `409` if it exists but wasn't an approved Reversal decision, `409` again on a second attempt to record an outcome for the same decision.

## Authentication

Same pattern as LedgerApi (`ARCHITECTURE.md` Section 5): every request needs an `X-Api-Key` header, and each key is granted **scopes**:

| Scope | Endpoints |
|---|---|
| `identity.customer.read` | `GET /v1/customers/{customerId}` |
| `identity.customer.write` | `POST /v1/customers`, `.../suspend`, `.../reactivate`, `.../close`, `.../mark-deceased` |
| `identity.kyc.manage` | `PATCH /v1/customers/{customerId}/kyc` |
| `identity.mandate.read` | `GET /v1/customers/{customerId}/mandates`, `GET /v1/accounts/{ledgerAccountId}/mandates`, `GET /v1/mandates/{mandateId}` |
| `identity.mandate.write` | `POST /v1/customers/{customerId}/mandates`, `POST /v1/mandates/{mandateId}/revoke` |
| `identity.authorization.check` | `POST /v1/authorization/requests`, `GET /v1/authorization/requests/{id}` (for `operation` values other than `"Reversal"`) |
| `identity.ops.reversal` | `POST /v1/authorization/requests` (only for `operation: "Reversal"`), `POST /v1/authorization/requests/{id}/reversal-outcome` |

`identity.ops.freeze` is defined (`docs/DESIGN.md` Section 5.2) but not wired to any endpoint yet - it is reserved for the Orchestrator's own outbox-consumption path (Section 2.5), which doesn't exist until the Orchestrator does.

`POST /v1/authorization/requests` is the one endpoint in this service gated by two different, mutually exclusive scopes depending on the request body's `operation` value, not by a single static policy - see [Reversal authorization](#reversal-authorization).

A missing or unknown key gets **401**; a valid key without the endpoint's scope gets **403**.

### Development keys

`appsettings.Development.json` configures two local-only clients. Never use these outside your machine.

| Client | Scopes |
|---|---|
| `dev-orchestrator` | customer.read, customer.write, kyc.manage, mandate.read, mandate.write, authorization.check |
| `dev-ops` | customer.read, customer.write, kyc.manage, mandate.read, mandate.write, ops.freeze, ops.reversal |

### Issuing a key

Only the SHA-256 hash of a key is configured, never the key itself:

```bash
KEY="idk_$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"; echo "$KEY"; printf %s "$KEY" | openssl dgst -sha256 -binary | base64
```

### Signing keys (authorization grants)

`appsettings.Development.json` also configures a **dev-only** ES256 (P-256) keypair under `SigningKeys`, clearly labeled, committed openly the same way the dev API key hashes above are. Unlike an API key, there's no "hash" equivalent for a signing key - the private key material itself has to be present to sign - so this is the actual key, never a real secret, never used outside this repository's own dev environment. The test suite uses its own, separate test-only keypair (`tests/.../Fixtures/TestSigningKey.cs`).

**This key cannot be used in Production.** `SigningKeyOptionsValidator` fails startup (`ValidateOnStart`) if the app runs with `ASPNETCORE_ENVIRONMENT=Production` and either the dev key or the test key is configured - checked by the key's actual public-key material (`docs/DESIGN.md` Appendix C, `#TBD-16`), not by its `kid` label, so relabeling it doesn't slip past the check.

`SigningKeys` is keyed by `kid` and shaped as a list from day one (`ActiveKid` plus `Keys`), so a second key can be added alongside the active one later for planned rotation (`docs/DESIGN.md` Section 4.3.1) without a breaking config change - this build only ever configures and uses one. Generate your own production keypair with:

```bash
openssl ecparam -name prime256v1 -genkey -noout -out private.pem
openssl pkcs8 -topk8 -nocrypt -in private.pem -out private-pkcs8.pem   # PrivateKeyPem value
openssl ec -in private.pem -pubout -out public.pem                     # for whoever verifies the grant later
```

## Logging and audit

Every request gets a correlation id (`X-Correlation-ID`), the same pattern as LedgerApi. Audit rows (`audit_events`) are written in the same database transaction as the change they describe. Nothing logs a raw BVN/NIN - this build does not even store one yet (see `Entities/CustomerIdentityData.cs`). The signed grant token is never logged or persisted anywhere - only its decision id (`jti`).

## Deployment

The `Dockerfile` builds two images:

```bash
docker build -t identityapi .                                   # the API
docker build --target migrator -t identityapi-migrator .        # applies pending migrations, then exits
```

GitHub Actions (`.github/workflows/ci.yml`) runs the tests and builds both images on every pull request and push to `main`. The `deploy` job mirrors LedgerApi's shape but is **not wired up** - it needs the owner's real Azure resource names and a configured federated credential before it can run (see the comments in `ci.yml`).

## Project layout

```
src/Garbati.IdentityApi/
  Controllers/      HTTP endpoints
  Services/         Business logic (CustomerService, MandateService, AuthorizationService)
  Entities/         Customer, CustomerIdentityData, KycVerification, KycTierPolicy, Mandate(Permission),
                     MandateApprovalPolicy, AuthorizationDecision, AuditEvent, OutboxMessage
  Data/             IdentityDbContext and model configuration
  Migrations/       EF Core migrations
  Validation/       Request validators
  Middleware/       Correlation id, exception-to-status mapping
  Auditing/         Audit trail (IAuditLogger stages rows on the request's DbContext)
  Authorization/    API-key authentication and scopes
  Signing/          ES256 grant signing (GrantSigner, SigningKeyOptions)
  Contracts/        Request/response records, and the outbox message payload shape
tests/Garbati.IdentityApi.Tests/
  Services/         Integration tests against PostgreSQL (Testcontainers)
  Controllers/      Status-code mapping tests with a stub service
  Validation/       Validator unit tests
  Authorization/    API-key/scope and controller-authorization tests
  Data/             Check-constraint and migration/schema tests
  Auditing/         Audit logger tests
docs/DESIGN.md      Design document and decisions log
```

## Out of scope

Planned key rotation (4.3.1) and the emergency key-compromise procedure (4.3.2) themselves (only a config shape that doesn't block adding them later). Real LedgerApi account-existence/transaction-reference calls (LedgerApi has no code to call yet). Any `PendingApprovalRequest`-style approval-collection workflow for reversals, any lien/auto-sweep mechanism, and the `DISPUTE` reversal reason code - all three deliberately out of scope per `docs/DESIGN.md` Section 8 (decisions #30/#32/#34). The ops channel itself (its maker-checker UI/process) - it doesn't exist and isn't designed here. See `docs/DESIGN.md` Appendices B-E for the full history of what each build task added.
