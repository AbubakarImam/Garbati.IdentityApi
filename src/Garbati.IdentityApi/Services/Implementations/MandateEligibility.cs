using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Microsoft.EntityFrameworkCore;

namespace Garbati.IdentityApi.Services;

// Extracted from AuthorizationService.RequestAuthorizationAsync (T-017) so the T-018 joint-approval flow
// can reuse the exact same customer/KYC/mandate eligibility logic - at PendingApprovalRequest creation
// time (checking the initiator) and again at threshold-completion time (checking the requester fresh,
// DESIGN.md Section 3.8.1 phase 2/decision #9) - without duplicating it. This is a static method, not an
// injected service, specifically so AuthorizationService's constructor and every existing call site
// (including every test in AuthorizationServiceTests.cs) stays untouched - only its inline body changes,
// to call this instead, with identical resulting behavior, reason codes and check order.
internal static class MandateEligibility
{
    public static async Task<(string? ReasonCode, Mandate? Mandate)> CheckAsync(
        IdentityDbContext dbContext,
        string customerId,
        string ledgerAccountId,
        MandateOperation operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var customer = await dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);

        string? reasonCode =
            customer is null ? AuthorizationDenialReasons.CustomerNotFound
            : customer.CustomerStatus != CustomerStatus.Active ? AuthorizationDenialReasons.CustomerNotActive
            : customer.KycStatus != KycStatus.Verified || customer.KycTier is null ? AuthorizationDenialReasons.CustomerNotVerified
            : null;

        // KYC-tier eligibility: the placeholder tier-policy lookup T-016 built (KycTierPolicies -
        // configurable policy data, decision #11), not a bespoke rules engine for this task.
        if (reasonCode is null)
        {
            var tierPolicy = await dbContext.KycTierPolicies.AsNoTracking()
                .Where(p => p.JurisdictionCode == customer!.JurisdictionCode && p.TierCode == customer.KycTier
                         && p.EffectiveFrom <= now && (p.EffectiveTo == null || p.EffectiveTo > now))
                .FirstOrDefaultAsync(cancellationToken);

            if (tierPolicy is null || !tierPolicy.EligibleOperations.Contains(operation.ToString()))
            {
                reasonCode = AuthorizationDenialReasons.OperationNotEligibleForKycTier;
            }
        }

        Mandate? mandate = null;
        if (reasonCode is null)
        {
            mandate = await dbContext.Mandates.AsNoTracking()
                .Include(m => m.Permissions)
                .FirstOrDefaultAsync(
                    m => m.CustomerId == customer!.Id && m.LedgerAccountId == ledgerAccountId
                         && m.Status == MandateStatus.Active,
                    cancellationToken);

            reasonCode = mandate switch
            {
                null => AuthorizationDenialReasons.NoActiveMandate,
                _ when !mandate.Permissions.Any(p => p.Operation == operation) => AuthorizationDenialReasons.OperationNotPermittedByMandate,
                _ => null,
            };
        }

        return (reasonCode, mandate);
    }
}
