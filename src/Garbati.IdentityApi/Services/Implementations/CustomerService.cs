using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Outbox;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Garbati.IdentityApi.Services;

public class CustomerService(IdentityDbContext dbContext, IAuditLogger auditLogger, ICurrentClient currentClient) : ICustomerService
{
    private const int MaxCustomerIdGenerationAttempts = 5;

    // Fingerprint of the fields that define the customer being created, so a reused idempotency key with
    // a different body is detected (decision #29, mirrors LedgerApi's AccountService.HashRequest).
    private static string HashRequest(CreateCustomerRequest request)
    {
        var fields = JsonSerializer.Serialize(new[]
        {
            request.FullName,
            request.JurisdictionCode,
            request.CustomerReference ?? "",
            request.Email ?? "",
            request.PhoneNumber ?? "",
        });
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(fields)));
    }

    private static CustomerResponse ToResponse(Customer customer) => new(
        customer.CustomerId,
        customer.CustomerReference,
        customer.CustomerStatus.ToString(),
        customer.KycStatus.ToString(),
        customer.KycTier,
        customer.JurisdictionCode,
        customer.CreatedAt,
        customer.UpdatedAt);

    // We generate the CustomerId ourselves (decision #19/#16) and retry on the rare collision, the same
    // shape as LedgerApi's account-number generation (AccountService.CreateAccountAsync). The idempotency
    // key (decision #29) shares the same retry loop, keyed off which unique constraint fired - exactly
    // the scheme LedgerApi's decision #43 uses for POST /api/accounts.
    public async Task<CustomerResponse> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default)
    {
        var clientId = currentClient.ClientId;
        var requestHash = HashRequest(request);

        for (var attempt = 1; attempt <= MaxCustomerIdGenerationAttempts; attempt++)
        {
            var now = DateTimeOffset.UtcNow;

            var customer = new Customer
            {
                CustomerId = CustomerIdGenerator.Generate(),
                CustomerReference = request.CustomerReference,
                CustomerStatus = CustomerStatus.Active,
                KycStatus = KycStatus.Pending,
                JurisdictionCode = request.JurisdictionCode,
                CreatedAt = now,
                UpdatedAt = now,
                IdempotencyKey = request.IdempotencyKey,
                InitiatedBy = clientId,
                RequestHash = requestHash,
                IdentityData = new CustomerIdentityData
                {
                    FullName = request.FullName,
                    Email = request.Email,
                    PhoneNumber = request.PhoneNumber,
                    CreatedAt = now,
                    UpdatedAt = now,
                },
            };

            await dbContext.Customers.AddAsync(customer, cancellationToken);

            var audit = auditLogger.Record(new AuditEvent
            {
                Action = AuditActions.CustomerCreated,
                EntityType = nameof(Customer),
                EntityId = customer.CustomerId,
                Status = customer.CustomerStatus.ToString(),
            });

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return ToResponse(customer);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } pgEx)
            {
                // Detach everything this attempt staged, or the retry would try to save it a second time
                // alongside the next attempt's rows. The dependent (IdentityData) must be detached before
                // its principal (Customer) - detaching the principal first leaves EF trying to null out a
                // required, non-nullable foreign key on the now-orphaned dependent, which throws.
                dbContext.Entry(customer.IdentityData).State = EntityState.Detached;
                dbContext.Entry(customer).State = EntityState.Detached;
                dbContext.Entry(audit).State = EntityState.Detached;

                if (pgEx.ConstraintName == "ix_customers_initiated_by_idempotency_key")
                {
                    var existingCustomer = await dbContext.Customers.AsNoTracking().FirstOrDefaultAsync(
                        c => c.InitiatedBy == clientId && c.IdempotencyKey == request.IdempotencyKey, cancellationToken);

                    if (existingCustomer is null)
                        throw new InvalidOperationException("Error retrieving the result of the existing idempotency key.");

                    // Same key, different request: refuse rather than report the old customer as this one's (409).
                    if (existingCustomer.RequestHash != requestHash)
                        throw new DuplicateIdempotencyKeyException(
                            $"Idempotency key '{request.IdempotencyKey}' was already used for a different request.");

                    return ToResponse(existingCustomer);
                }
                // Otherwise the collision was on CustomerId (ix_customers_customer_id): loop around and
                // try another one.
            }
        }

        throw new InvalidOperationException($"Could not allocate a unique CustomerId after {MaxCustomerIdGenerationAttempts} attempts.");
    }

    public async Task<CustomerResponse?> GetCustomerAsync(string customerId, CancellationToken cancellationToken = default)
    {
        var customer = await dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        return customer is null ? null : ToResponse(customer);
    }

    public async Task<CustomerResponse> RecordKycDecisionAsync(
        string customerId, KycDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await GetTrackedCustomerOrThrowAsync(customerId, cancellationToken);

        if (customer.CustomerStatus is CustomerStatus.Closed or CustomerStatus.Deceased)
        {
            throw new InvalidCustomerStatusTransitionException(
                $"Cannot record a KYC decision for a customer whose status is {customer.CustomerStatus}.");
        }

        var newStatus = Enum.Parse<KycStatus>(request.KycStatus, ignoreCase: true);
        var now = DateTimeOffset.UtcNow;

        dbContext.KycVerifications.Add(new KycVerification
        {
            CustomerId = customer.Id,
            VerificationType = request.VerificationType,
            Provider = request.Provider,
            ProviderReference = request.ProviderReference,
            Status = newStatus,
            ResultCode = request.ResultCode,
            VerifiedAt = newStatus == KycStatus.Verified ? now : null,
            ExpiresAt = request.ExpiresAt,
            CreatedAt = now,
        });

        var previousStatus = customer.KycStatus;
        var previousTier = customer.KycTier;
        customer.KycStatus = newStatus;
        // A tier is only meaningful once verified; a Rejected/Expired/Pending decision does not change it,
        // and - per the guard below - never needs the evidence check either (T-044).
        if (newStatus == KycStatus.Verified)
        {
            await EnsureIdentityNumberEvidenceIsMetAsync(customer, request.KycTier!, cancellationToken);
            customer.KycTier = request.KycTier;
        }
        customer.UpdatedAt = now;

        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.KycDecisionRecorded,
            EntityType = nameof(Customer),
            EntityId = customer.CustomerId,
            Status = newStatus.ToString(),
            Details = $"KycStatus {previousStatus} -> {newStatus}; KycTier {previousTier ?? "(none)"} -> {customer.KycTier ?? "(none)"}",
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(customer);
    }

    // T-044 (garbati-platform D-26): the narrowly-scoped half of KycTierPolicy.RequiredEvidence this
    // task enforces - AtLeastOneIdentityNumber/BothIdentityNumbers only (Entities/KycEvidenceTokens.cs).
    // Deliberately does NOT look at SelfDeclaredIdentity/GovernmentId/SourceOfFunds, which have no
    // backing data anywhere in the system yet - see the task file's "Enforcement scope" section for why
    // enforcing those now would make Tier2/Tier3 permanently ungrantable.
    //
    // "Currently effective" mirrors KycTierPolicy's own EffectiveFrom/EffectiveTo shape; no matching row
    // at all (wrong jurisdiction/tier, or a tier that was never seeded) is treated as "nothing to check" -
    // the same as today's behaviour before this task existed (RequiredEvidence was never read anywhere),
    // not a new 500 and not an invented new failure mode.
    private async Task EnsureIdentityNumberEvidenceIsMetAsync(Customer customer, string tierCode, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var policy = await dbContext.KycTierPolicies.AsNoTracking()
            .Where(p => p.JurisdictionCode == customer.JurisdictionCode && p.TierCode == tierCode
                && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now))
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(cancellationToken);

        if (policy is null)
        {
            return;
        }

        var identityData = customer.IdentityData!; // every customer has one, created alongside it in CreateCustomerAsync
        var hasBvn = identityData.Bvn is not null;
        var hasNin = identityData.Nin is not null;

        if (policy.RequiredEvidence.Contains(KycEvidenceTokens.BothIdentityNumbers) && !(hasBvn && hasNin))
        {
            throw new KycTierEvidenceNotMetException(
                $"KycTier {tierCode} requires both Bvn and Nin to be on file for this customer.");
        }

        if (policy.RequiredEvidence.Contains(KycEvidenceTokens.AtLeastOneIdentityNumber) && !(hasBvn || hasNin))
        {
            throw new KycTierEvidenceNotMetException(
                $"KycTier {tierCode} requires at least one of Bvn or Nin to be on file for this customer.");
        }
    }

    // T-044 (garbati-platform D-26): PATCH /v1/customers/{customerId}/identity-numbers. Its own endpoint,
    // separate from UpdateDetailsAsync (T-035), because Bvn/Nin are set-once, not freely editable - see
    // the task file for why a channel that lets someone silently swap a stored BVN/NIN is a fraud vector.
    // No admin-override path exists in this task; a real correction process is a deliberate future
    // follow-up, not a side effect of this endpoint.
    //
    // No-op choice (task file: "your call, document which you chose and why"): resubmitting the exact
    // value already on file writes NO audit row. Chosen because the audit trail should describe an
    // actual change, and "the caller re-sent the same correct value" is not one - the same reasoning
    // ChangeStatusAsync's own idempotent-repeat branches (Suspend/Reactivate) already use, which likewise
    // return early without auditing anything when nothing actually changed.
    public async Task<CustomerResponse> SubmitIdentityNumbersAsync(
        string customerId, SubmitIdentityNumbersRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await GetTrackedCustomerOrThrowAsync(customerId, cancellationToken);
        var identityData = customer.IdentityData!; // every customer has one, created alongside it in CreateCustomerAsync

        // Only the field name is ever recorded (e.g. "Bvn set") - never the submitted value. Unlike
        // UpdateDetailsAsync's "FullName old -> new" Details string, the value itself is exactly what
        // ARCHITECTURE.md Section 5 and DESIGN.md Section 6.2 forbid logging, and that discipline applies
        // to this durable AuditEvent.Details text too, not only to Serilog request logs.
        var changes = new List<string>();

        if (request.Bvn is not null)
        {
            if (identityData.Bvn is null)
            {
                identityData.Bvn = request.Bvn;
                changes.Add("Bvn set");
            }
            else if (identityData.Bvn != request.Bvn)
            {
                throw new IdentityNumberAlreadySetException(
                    "Bvn is already set for this customer and cannot be changed via this endpoint.");
            }
            // else: the same value was resubmitted - idempotent no-op, nothing to change or record.
        }

        if (request.Nin is not null)
        {
            if (identityData.Nin is null)
            {
                identityData.Nin = request.Nin;
                changes.Add("Nin set");
            }
            else if (identityData.Nin != request.Nin)
            {
                throw new IdentityNumberAlreadySetException(
                    "Nin is already set for this customer and cannot be changed via this endpoint.");
            }
        }

        if (changes.Count == 0)
        {
            // Every supplied field matched what was already stored - a pure no-op. Nothing was mutated on
            // the tracked entity, so there is nothing to save and nothing to audit.
            return ToResponse(customer);
        }

        identityData.UpdatedAt = DateTimeOffset.UtcNow;

        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.IdentityNumberSubmitted,
            EntityType = nameof(Customer),
            EntityId = customer.CustomerId,
            Status = customer.CustomerStatus.ToString(),
            Details = string.Join("; ", changes),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(customer);
    }

    // T-035 (garbati-platform D-19): a partial update of CustomerIdentityData - only the fields the
    // caller actually supplied (non-null) change. No status check (unlike RecordKycDecisionAsync): the
    // task file scopes this to the three plain contact/identity fields only, with no interaction with
    // CustomerStatus/KycStatus/KycTier at all, so a Suspended customer's details can still be corrected
    // (e.g. a typo found during the very review that suspended them).
    public async Task<CustomerResponse> UpdateDetailsAsync(
        string customerId, UpdateCustomerDetailsRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await GetTrackedCustomerOrThrowAsync(customerId, cancellationToken);
        var identityData = customer.IdentityData!; // every customer has one, created alongside it in CreateCustomerAsync

        // One "field old -> new" entry per supplied field, same shape as RecordKycDecisionAsync's
        // Details string, so the audit row says exactly what changed without needing a second query.
        var changes = new List<string>();

        if (request.FullName is not null)
        {
            changes.Add($"FullName {identityData.FullName} -> {request.FullName}");
            identityData.FullName = request.FullName;
        }

        if (request.Email is not null)
        {
            changes.Add($"Email {identityData.Email ?? "(none)"} -> {request.Email}");
            identityData.Email = request.Email;
        }

        if (request.PhoneNumber is not null)
        {
            changes.Add($"PhoneNumber {identityData.PhoneNumber ?? "(none)"} -> {request.PhoneNumber}");
            identityData.PhoneNumber = request.PhoneNumber;
        }

        identityData.UpdatedAt = DateTimeOffset.UtcNow;

        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.CustomerDetailsUpdated,
            EntityType = nameof(Customer),
            EntityId = customer.CustomerId,
            Status = customer.CustomerStatus.ToString(),
            Details = string.Join("; ", changes),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(customer);
    }

    // Active -> Suspended. Already-Suspended is treated as idempotent (returns the current state, 200);
    // Closed/Deceased is terminal and refuses with 409 (DESIGN.md Section 2.1: "No - terminal").
    public Task<CustomerResponse> SuspendCustomerAsync(
        string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            customerId, CustomerStatus.Suspended, AuditActions.CustomerSuspended, request.Reason,
            allowedFrom: [CustomerStatus.Active], idempotentFrom: [CustomerStatus.Suspended],
            cascade: false, cancellationToken);

    // Suspended -> Active. Already-Active is idempotent; Closed/Deceased refuses with 409 (terminal,
    // "reactivation is possible after review" only applies to Suspended - DESIGN.md Section 2.1).
    public Task<CustomerResponse> ReactivateCustomerAsync(
        string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            customerId, CustomerStatus.Active, AuditActions.CustomerReactivated, request.Reason,
            allowedFrom: [CustomerStatus.Suspended], idempotentFrom: [CustomerStatus.Active],
            cascade: false, cancellationToken);

    // Active or Suspended -> Closed. Terminal: unlike suspend/reactivate, a repeat is NOT treated as a
    // no-op - see InvalidCustomerStatusTransitionException's comment for why 409 was chosen here.
    public Task<CustomerResponse> CloseCustomerAsync(
        string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            customerId, CustomerStatus.Closed, AuditActions.CustomerClosed, request.Reason,
            allowedFrom: [CustomerStatus.Active, CustomerStatus.Suspended], idempotentFrom: [],
            cascade: true, cancellationToken);

    // Active or Suspended -> Deceased. Same terminal/409-on-repeat treatment as Close.
    public Task<CustomerResponse> MarkDeceasedAsync(
        string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
        ChangeStatusAsync(
            customerId, CustomerStatus.Deceased, AuditActions.CustomerMarkedDeceased, request.Reason,
            allowedFrom: [CustomerStatus.Active, CustomerStatus.Suspended], idempotentFrom: [],
            cascade: true, cancellationToken);

    private async Task<CustomerResponse> ChangeStatusAsync(
        string customerId,
        CustomerStatus newStatus,
        string auditAction,
        string? reason,
        IReadOnlyCollection<CustomerStatus> allowedFrom,
        IReadOnlyCollection<CustomerStatus> idempotentFrom,
        bool cascade,
        CancellationToken cancellationToken)
    {
        var customer = await GetTrackedCustomerOrThrowAsync(customerId, cancellationToken);

        if (idempotentFrom.Contains(customer.CustomerStatus))
        {
            return ToResponse(customer);
        }

        if (!allowedFrom.Contains(customer.CustomerStatus))
        {
            throw new InvalidCustomerStatusTransitionException(
                $"Cannot move customer '{customerId}' from {customer.CustomerStatus} to {newStatus}.");
        }

        var previousStatus = customer.CustomerStatus;
        customer.CustomerStatus = newStatus;
        customer.UpdatedAt = DateTimeOffset.UtcNow;

        auditLogger.Record(new AuditEvent
        {
            Action = auditAction,
            EntityType = nameof(Customer),
            EntityId = customer.CustomerId,
            Status = newStatus.ToString(),
            Details = reason is null
                ? $"{previousStatus} -> {newStatus}"
                : $"{previousStatus} -> {newStatus}; reason: {reason}",
        });

        if (cascade)
        {
            await CascadeMandateRevocationAndFreezeAsync(customer, reason, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(customer);
    }

    // DESIGN.md Section 2.1, decision #14: becoming Closed or Deceased revokes every mandate this
    // customer holds, and queues a freeze (an OutboxMessages row, Section 2.5) for every ledger account
    // where it was their only active mandate. T-016 left this as a documented no-op because Mandates
    // didn't exist yet; T-017 (this task) wires it up for real now that they do.
    //
    // Everything below is staged on the same dbContext as the status change in ChangeStatusAsync and
    // commits (or rolls back) together with it in that method's single SaveChangesAsync call - there is
    // no separate transaction here.
    private async Task CascadeMandateRevocationAndFreezeAsync(Customer customer, string? reason, CancellationToken cancellationToken)
    {
        var activeMandates = await dbContext.Mandates
            .Where(m => m.CustomerId == customer.Id && m.Status == MandateStatus.Active)
            .ToListAsync(cancellationToken);

        if (activeMandates.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var actor = currentClient.ClientId ?? "system";
        var cascadeReason = reason is null
            ? $"Cascaded from customer status change to {customer.CustomerStatus}."
            : $"Cascaded from customer status change to {customer.CustomerStatus}: {reason}";

        foreach (var mandate in activeMandates)
        {
            mandate.Status = MandateStatus.Revoked;
            mandate.RevokedAt = now;
            mandate.RevokedBy = actor;
            mandate.RevocationReason = cascadeReason;

            auditLogger.Record(new AuditEvent
            {
                Action = AuditActions.MandateRevoked,
                EntityType = nameof(Mandate),
                EntityId = mandate.MandateId,
                Status = mandate.Status.ToString(),
                Details = cascadeReason,
            });
        }

        // One freeze check per distinct account, not per mandate - a customer could in principle hold
        // more than one mandate on the same account only if earlier data allowed it, but either way the
        // "does anyone else still hold this account" question only needs asking once per account.
        foreach (var ledgerAccountId in activeMandates.Select(m => m.LedgerAccountId).Distinct())
        {
            var otherHolderStillActive = await dbContext.Mandates.AnyAsync(
                m => m.LedgerAccountId == ledgerAccountId && m.Status == MandateStatus.Active && m.CustomerId != customer.Id,
                cancellationToken);

            // A joint account with a surviving active holder is NOT frozen (DESIGN.md Section 2.1's
            // table) - only this customer's own mandate was revoked above.
            if (otherHolderStillActive)
            {
                continue;
            }

            var payload = new AccountFreezeRequestedPayload(ledgerAccountId, customer.CustomerId, cascadeReason, now);
            dbContext.OutboxMessages.Add(new OutboxMessage
            {
                MessageType = OutboxMessageTypes.AccountFreezeRequested,
                Payload = JsonSerializer.Serialize(payload),
                Status = OutboxMessageStatus.Pending,
                CreatedAt = now,
            });

            auditLogger.Record(new AuditEvent
            {
                Action = AuditActions.AccountFreezeRequested,
                EntityType = nameof(OutboxMessage),
                EntityId = ledgerAccountId,
                Status = OutboxMessageStatus.Pending.ToString(),
                Details = cascadeReason,
            });
        }
    }

    private async Task<Customer> GetTrackedCustomerOrThrowAsync(string customerId, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers
            .Include(c => c.IdentityData)
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        return customer ?? throw new CustomerNotFoundException($"Customer '{customerId}' was not found.");
    }
}
