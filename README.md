# Identity API

The customer-identity and KYC service for GARBATI, built with ASP.NET Core 8, EF Core and PostgreSQL.

The design, including every trade-off, is in [docs/DESIGN.md](docs/DESIGN.md) - start there for the *why*. This README covers the *how*. See [`garbati-platform/ARCHITECTURE.md`](https://github.com/AbubakarImam/garbati-platform/blob/main/ARCHITECTURE.md) for how this service fits the rest of the platform: **Channel → Orchestrator → IdentityApi → Orchestrator → LedgerApi**.

## What this build covers

This is the **first of two build tasks** (`garbati-platform` T-016). It builds the project skeleton, `Customer` records and KYC - enough to create a customer and move them through KYC. It deliberately does **not** build `Mandates`, joint-account approval, or any token/grant-signing code - that is T-017, a separate follow-up. See `docs/DESIGN.md` Appendix B for the full list of build-time judgment calls this task made.

## Core pieces

- **Customer** - internal, generated `CustomerId` (never a BVN, NIN or ledger account number); `CustomerStatus` (`Active`/`Suspended`/`Deceased`/`Closed`) and `KycStatus`/`KycTier`.
- **CustomerIdentityData** - sensitive fields (name, contact) kept in their own table, separate from `Customer`'s operational fields.
- **KycVerification** - an append-only record of each verification attempt and its outcome.
- **KycTierPolicy** - configurable tier rules, not hardcoded. Seeded with **placeholder** Nigerian tiers only (see the migration); the real evidence/limits are still pending compliance confirmation.
- **AuditEvents** / **OutboxMessages** - a durable, append-only audit trail, and durable outbox rows for work (e.g. account freezes) that the Orchestrator will eventually consume. No Orchestrator exists yet, so no message has a consumer today - this is expected at this stage.

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
| `POST` | `/v1/customers` | Create a customer record (`CustomerStatus = Active`, `KycStatus = Pending`) |
| `GET` | `/v1/customers/{customerId}` | Redacted customer view - never raw `CustomerIdentityData` fields |
| `PATCH` | `/v1/customers/{customerId}/kyc` | Record a KYC verification attempt and status/tier decision |
| `POST` | `/v1/customers/{customerId}/suspend` | Suspend a customer (idempotent if already suspended) |
| `POST` | `/v1/customers/{customerId}/reactivate` | Reactivate a suspended customer (idempotent if already active) |
| `POST` | `/v1/customers/{customerId}/close` | Close a customer - terminal; a repeat attempt returns 409 |
| `POST` | `/v1/customers/{customerId}/mark-deceased` | Mark a customer deceased - terminal; a repeat attempt returns 409 |

`close` and `mark-deceased` are this task's own addition - `docs/DESIGN.md` Section 5.1 lists only `suspend`/`reactivate`; see Appendix B for why.

### Status codes

| Status | Meaning |
|---|---|
| `200` | Success |
| `400` | Request failed validation |
| `401` | Missing or unknown API key |
| `403` | The API key lacks the endpoint's scope |
| `404` | Customer not found |
| `409` | Customer status transition not allowed (e.g. closing an already-Closed customer) |
| `500` | Unexpected error |

### The Closed/Deceased cascade - known limitation

`docs/DESIGN.md` Section 2.1 (decision #14) says becoming `Closed` or `Deceased` should automatically revoke every mandate the customer holds and queue an account freeze for any account where it was their only active mandate. **This task builds the status transition, its audit event, and the target shape of the freeze message (`Contracts/Outbox/AccountFreezeRequestedPayload.cs`) - not the mandate lookup itself**, because `Mandates` does not exist until T-017. `CustomerService.CascadeMandateRevocationAndFreezeAsync` is a documented no-op today. T-017 must come back to that exact method.

## Authentication

Same pattern as LedgerApi (`ARCHITECTURE.md` Section 5): every request needs an `X-Api-Key` header, and each key is granted **scopes**:

| Scope | Endpoints |
|---|---|
| `identity.customer.read` | `GET /v1/customers/{customerId}` |
| `identity.customer.write` | `POST /v1/customers`, `.../suspend`, `.../reactivate`, `.../close`, `.../mark-deceased` |
| `identity.kyc.manage` | `PATCH /v1/customers/{customerId}/kyc` |

`identity.mandate.read`, `identity.mandate.write`, `identity.authorization.check` and `identity.ops.freeze` are defined (`docs/DESIGN.md` Section 5.2) but not wired to any endpoint yet - that is T-017.

A missing or unknown key gets **401**; a valid key without the endpoint's scope gets **403**.

### Development keys

`appsettings.Development.json` configures two local-only clients. Never use these outside your machine.

| Client | Scopes |
|---|---|
| `dev-orchestrator` | customer.read, customer.write, kyc.manage |
| `dev-ops` | customer.read, customer.write, kyc.manage, mandate.read, mandate.write, ops.freeze |

### Issuing a key

Only the SHA-256 hash of a key is configured, never the key itself:

```bash
KEY="idk_$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"; echo "$KEY"; printf %s "$KEY" | openssl dgst -sha256 -binary | base64
```

## Logging and audit

Every request gets a correlation id (`X-Correlation-ID`), the same pattern as LedgerApi. Audit rows (`audit_events`) are written in the same database transaction as the change they describe. Nothing logs a raw BVN/NIN - this build does not even store one yet (see `Entities/CustomerIdentityData.cs`).

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
  Services/         Business logic (CustomerService)
  Entities/         Customer, CustomerIdentityData, KycVerification, KycTierPolicy, AuditEvent, OutboxMessage
  Data/             IdentityDbContext and model configuration
  Migrations/       EF Core migrations
  Validation/       Request validators
  Middleware/       Correlation id, exception-to-status mapping
  Auditing/         Audit trail (IAuditLogger stages rows on the request's DbContext)
  Authorization/    API-key authentication and scopes
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

## Out of scope (T-017)

`Mandates`, `MandatePermissions`, the joint-account entities (`MandateApprovalPolicies`, `PendingApprovalRequests`, `ApprovalRecords`), any `/v1/mandates/*` or `/v1/authorization/*` endpoint, JWT/grant signing, and key-rotation/compromise configuration are explicitly out of scope for this build - see `docs/DESIGN.md`'s task-file reference and the T-016 pull request for why.
