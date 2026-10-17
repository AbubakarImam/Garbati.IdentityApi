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
        // A tier is only meaningful once verified; a Rejected/Expired/Pending decision does not change it.
        if (newStatus == KycStatus.Verified)
        {
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
