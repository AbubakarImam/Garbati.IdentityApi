using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Garbati.IdentityApi.Services;

public class MandateService(IdentityDbContext dbContext, IAuditLogger auditLogger, ICurrentClient currentClient) : IMandateService
{
    private const int MaxMandateIdGenerationAttempts = 5;

    private static MandateResponse ToResponse(Mandate mandate, string customerId) => new(
        mandate.MandateId,
        customerId,
        mandate.LedgerAccountId,
        mandate.Status.ToString(),
        mandate.Permissions.Select(p => p.Operation.ToString()).OrderBy(o => o).ToList(),
        mandate.CreatedAt,
        mandate.CreatedBy,
        mandate.RevokedAt,
        mandate.RevokedBy,
        mandate.RevocationReason);

    // DESIGN.md Section 2.2/2.2a/2.4. We generate the MandateId ourselves (same approach as
    // CustomerIdGenerator) and retry on the rare collision, the same shape as CustomerService's
    // CreateCustomerAsync.
    //
    // Account-existence check (Section 2.4): a mandate cannot become Active referencing a
    // LedgerAccountId that hasn't been confirmed to exist. The real check is a narrow, read-only call to
    // LedgerApi - but LedgerApi has no code yet (see the task file), so there is nothing to actually
    // call. TODO: once LedgerApi exists and exposes a read-only account-existence endpoint, call it here
    // before accepting request.LedgerAccountId. Until then it is accepted as an opaque string the caller
    // asserts exists - this is a documented gap, not a faked call to a service that isn't there.
    public async Task<MandateResponse> AttachMandateAsync(
        string customerId, AttachMandateRequest request, CancellationToken cancellationToken = default)
    {
        var customer = await dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken)
            ?? throw new CustomerNotFoundException($"Customer '{customerId}' was not found.");

        var now = DateTimeOffset.UtcNow;
        var actor = currentClient.ClientId ?? "unknown";
        var operations = request.Operations
            .Select(o => Enum.Parse<MandateOperation>(o, ignoreCase: true))
            .Distinct()
            .ToList();

        for (var attempt = 1; attempt <= MaxMandateIdGenerationAttempts; attempt++)
        {
            var mandate = new Mandate
            {
                MandateId = MandateIdGenerator.Generate(),
                CustomerId = customer.Id,
                LedgerAccountId = request.LedgerAccountId,
                Status = MandateStatus.Active,
                CreatedAt = now,
                CreatedBy = actor,
                Permissions = operations.Select(op => new MandatePermission { Operation = op }).ToList(),
            };
            await dbContext.Mandates.AddAsync(mandate, cancellationToken);

            // Created the moment this account's first mandate is created (DESIGN.md Section 2.2a).
            var existingPolicy = await dbContext.MandateApprovalPolicies
                .FirstOrDefaultAsync(p => p.LedgerAccountId == request.LedgerAccountId, cancellationToken);
            var createdNewPolicy = existingPolicy is null;
            if (createdNewPolicy)
            {
                await dbContext.MandateApprovalPolicies.AddAsync(new MandateApprovalPolicy
                {
                    LedgerAccountId = request.LedgerAccountId,
                    RequiredApprovals = 1,
                    CreatedAt = now,
                    LastChangedAt = now,
                    LastChangedBy = null,
                }, cancellationToken);
            }

            var audit = auditLogger.Record(new AuditEvent
            {
                Action = AuditActions.MandateCreated,
                EntityType = nameof(Mandate),
                EntityId = mandate.MandateId,
                Status = mandate.Status.ToString(),
                Details = $"CustomerId {customer.CustomerId}; LedgerAccountId {mandate.LedgerAccountId}; " +
                          $"Operations {string.Join(",", operations)}",
            });

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return ToResponse(mandate, customer.CustomerId);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" } pgEx)
            {
                // Detach everything this attempt staged before retrying/failing, the same reasoning as
                // CustomerService.CreateCustomerAsync - dependents before principals. ToList() first:
                // detaching a permission triggers EF's navigation fixup, which removes it from
                // mandate.Permissions - mutating the very collection a plain foreach would be iterating.
                foreach (var permission in mandate.Permissions.ToList())
                {
                    dbContext.Entry(permission).State = EntityState.Detached;
                }
                dbContext.Entry(mandate).State = EntityState.Detached;
                dbContext.Entry(audit).State = EntityState.Detached;
                if (createdNewPolicy)
                {
                    dbContext.ChangeTracker.Entries<MandateApprovalPolicy>()
                        .Where(e => e.State == EntityState.Added)
                        .ToList()
                        .ForEach(e => e.State = EntityState.Detached);
                }

                if (pgEx.ConstraintName == "ix_mandates_customer_account_active")
                {
                    throw new DuplicateActiveMandateException(
                        $"Customer '{customerId}' already has an active mandate on account '{request.LedgerAccountId}'.");
                }
                // Otherwise the collision was on MandateId (ix_mandates_mandate_id): loop and regenerate.
            }
        }

        throw new InvalidOperationException($"Could not allocate a unique MandateId after {MaxMandateIdGenerationAttempts} attempts.");
    }

    public async Task<IReadOnlyList<MandateResponse>> ListMandatesForCustomerAsync(
        string customerId, CancellationToken cancellationToken = default)
    {
        var customer = await dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken)
            ?? throw new CustomerNotFoundException($"Customer '{customerId}' was not found.");

        var mandates = await dbContext.Mandates.AsNoTracking()
            .Include(m => m.Permissions)
            .Where(m => m.CustomerId == customer.Id)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        return mandates.Select(m => ToResponse(m, customer.CustomerId)).ToList();
    }

    // DESIGN.md Section 5.1: "the view a channel needs to show 'who else is on this account'." No
    // customer/account record is validated against here - LedgerAccountId is a plain opaque string
    // (Section 2.2) - so an account with no mandates at all just returns an empty list, not 404.
    public async Task<IReadOnlyList<MandateResponse>> ListActiveHoldersForAccountAsync(
        string ledgerAccountId, CancellationToken cancellationToken = default)
    {
        var mandates = await dbContext.Mandates.AsNoTracking()
            .Include(m => m.Permissions)
            .Include(m => m.Customer)
            .Where(m => m.LedgerAccountId == ledgerAccountId && m.Status == MandateStatus.Active)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken);

        return mandates.Select(m => ToResponse(m, m.Customer.CustomerId)).ToList();
    }

    public async Task<MandateResponse?> GetMandateAsync(string mandateId, CancellationToken cancellationToken = default)
    {
        var mandate = await dbContext.Mandates.AsNoTracking()
            .Include(m => m.Permissions)
            .Include(m => m.Customer)
            .FirstOrDefaultAsync(m => m.MandateId == mandateId, cancellationToken);

        return mandate is null ? null : ToResponse(mandate, mandate.Customer.CustomerId);
    }

    public async Task<MandateResponse> RevokeMandateAsync(
        string mandateId, RevokeMandateRequest request, CancellationToken cancellationToken = default)
    {
        var mandate = await dbContext.Mandates
            .Include(m => m.Permissions)
            .Include(m => m.Customer)
            .FirstOrDefaultAsync(m => m.MandateId == mandateId, cancellationToken)
            ?? throw new MandateNotFoundException($"Mandate '{mandateId}' was not found.");

        if (mandate.Status == MandateStatus.Revoked)
        {
            // A mandate, once revoked, is retained for history and never reactivated (DESIGN.md Section
            // 2.2) - a repeat revoke call is a safe no-op (200), not an error. Unlike Customer's
            // Closed/Deceased (which T-016 made terminal with 409 on a repeat), nothing here is lost by
            // treating a second revoke as idempotent: the original reason/actor/timestamp are preserved
            // untouched.
            return ToResponse(mandate, mandate.Customer.CustomerId);
        }

        var now = DateTimeOffset.UtcNow;
        mandate.Status = MandateStatus.Revoked;
        mandate.RevokedAt = now;
        mandate.RevokedBy = currentClient.ClientId ?? "unknown";
        mandate.RevocationReason = request.Reason;

        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.MandateRevoked,
            EntityType = nameof(Mandate),
            EntityId = mandate.MandateId,
            Status = mandate.Status.ToString(),
            Details = $"reason: {request.Reason}",
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(mandate, mandate.Customer.CustomerId);
    }
}
