using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Garbati.IdentityApi.Services;

public class CustomerService(IdentityDbContext dbContext, IAuditLogger auditLogger) : ICustomerService
{
    private const int MaxCustomerIdGenerationAttempts = 5;

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
    // shape as LedgerApi's account-number generation (AccountService.CreateAccountAsync).
    public async Task<CustomerResponse> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default)
    {
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
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
            {
                // Detach everything this attempt staged, or the retry would try to save it a second time
                // alongside the next attempt's rows. The only unique constraint on Customer is CustomerId
                // (ix_customers_customer_id), so any 23505 here means the generated id collided; try again
                // with a freshly generated one.
                dbContext.Entry(customer).State = EntityState.Detached;
                dbContext.Entry(customer.IdentityData).State = EntityState.Detached;
                dbContext.Entry(audit).State = EntityState.Detached;
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

    // DESIGN.md Section 2.1, decision #14: becoming Closed or Deceased should revoke every mandate this
    // customer holds, and queue a freeze (an OutboxMessages row, Section 2.5) for every ledger account
    // where it was their only active mandate.
    //
    // T-016 (this task) deliberately does not build Mandates - that is T-017's job (see the task file's
    // explicit scope boundary). There is therefore nothing to query here yet: no mandates exist to
    // revoke, and so no OutboxMessage is written on this path today. This is a documented gap, not an
    // oversight - the task file explicitly forbids inventing a fake mandate check to make this look more
    // finished than it is.
    //
    // T-017 MUST come back to this exact method and:
    //   1. Query every Active Mandate held by this customer; set each to Revoked (RevokedAt/RevokedBy/
    //      RevocationReason) in the same database transaction as the status change above.
    //   2. For every distinct LedgerAccountId among those mandates, check whether any OTHER customer
    //      still holds an Active mandate on it. If not, this customer was its only active holder: add an
    //      OutboxMessage (MessageType = OutboxMessageTypes.AccountFreezeRequested, Payload = the
    //      serialized shape of Contracts.Outbox.AccountFreezeRequestedPayload) so the Orchestrator can
    //      carry out the actual freeze (IdentityApi never calls LedgerApi to change state - Section 2.5).
    //   3. A joint account with a surviving active holder must NOT get a freeze message (Section 2.1).
    private Task CascadeMandateRevocationAndFreezeAsync(Customer customer, string? reason, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task<Customer> GetTrackedCustomerOrThrowAsync(string customerId, CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers
            .Include(c => c.IdentityData)
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        return customer ?? throw new CustomerNotFoundException($"Customer '{customerId}' was not found.");
    }
}
