using System.Globalization;
using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Garbati.IdentityApi.Signing;
using Microsoft.EntityFrameworkCore;

namespace Garbati.IdentityApi.Services;

// DESIGN.md Section 3 (3.1-3.7 only - Section 3.8's joint-account collect-approvals workflow is T-018).
// This task's authorization endpoint only ever has to satisfy M = 1: the requester's own mandate is
// always enough, because nothing yet lets an account have RequiredApprovals > 1 (see
// AuthorizationDenialReasons.ApprovalPolicyNotSingleHolder - that check exists so the one assumption the
// rest of this method depends on is verified, not assumed).
//
// Fail-closed (decision #10) is structural here, not a special code path: every lookup that can throw
// (a DB error, an unexpected exception) simply propagates to the caller as an unhandled exception -
// mapped by ExceptionHandlingMiddleware to 500 - rather than being caught and treated as approval. There
// is no catch block anywhere in this class that turns an error into a grant.
public class AuthorizationService(IdentityDbContext dbContext, IAuditLogger auditLogger, IGrantSigner grantSigner) : ITransactionAuthorizationService
{
    private const int GrantLifetimeSeconds = 30;

    public async Task<AuthorizationResponse> RequestAuthorizationAsync(
        AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var decisionId = Guid.NewGuid().ToString();
        var operation = Enum.Parse<MandateOperation>(request.Operation, ignoreCase: true);
        var amount = request.Amount.ToString("G29", CultureInfo.InvariantCulture);

        var customer = await dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == request.RequestedBy, cancellationToken);

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
                    m => m.CustomerId == customer!.Id && m.LedgerAccountId == request.DebitAccountId
                         && m.Status == MandateStatus.Active,
                    cancellationToken);

            reasonCode = mandate switch
            {
                null => AuthorizationDenialReasons.NoActiveMandate,
                _ when !mandate.Permissions.Any(p => p.Operation == operation) => AuthorizationDenialReasons.OperationNotPermittedByMandate,
                _ => null,
            };
        }

        if (reasonCode is null)
        {
            // Defensive, not decorative (task file): this is the one assumption the entire single-holder
            // flow depends on. No "if (requiredApprovals > 1)" branch exists anywhere - that belongs to
            // T-018, which this task must not anticipate.
            var policy = await dbContext.MandateApprovalPolicies.AsNoTracking()
                .FirstOrDefaultAsync(p => p.LedgerAccountId == request.DebitAccountId, cancellationToken);

            if (policy is null || policy.RequiredApprovals != 1)
            {
                reasonCode = AuthorizationDenialReasons.ApprovalPolicyNotSingleHolder;
            }
        }

        if (reasonCode is not null)
        {
            await PersistDecisionAsync(decisionId, request, amount, now, approved: false, reasonCode, mandateId: null, cancellationToken);
            throw new AuthorizationDeniedException(
                decisionId, reasonCode, $"Authorization denied for decision '{decisionId}': {reasonCode}.");
        }

        var expiresAt = now.AddSeconds(GrantLifetimeSeconds);
        var grant = grantSigner.Sign(new GrantClaims(
            Jti: decisionId,
            Sub: customer!.CustomerId,
            Operation: operation.ToString(),
            DebitAccountId: request.DebitAccountId,
            CreditAccountId: request.CreditAccountId,
            Amount: amount,
            Currency: request.Currency,
            IdempotencyKey: request.IdempotencyKey,
            MandateId: mandate!.MandateId,
            IssuedAt: now,
            ExpiresAt: expiresAt));

        var decision = await PersistDecisionAsync(
            decisionId, request, amount, now, approved: true, reasonCode: null, mandate.MandateId, cancellationToken, expiresAt);

        return new AuthorizationResponse(ToDecisionResponse(decision), grant.Token, expiresAt);
    }

    public async Task<AuthorizationDecisionResponse?> GetDecisionAsync(string decisionId, CancellationToken cancellationToken = default)
    {
        var decision = await dbContext.AuthorizationDecisions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DecisionId == decisionId, cancellationToken);
        return decision is null ? null : ToDecisionResponse(decision);
    }

    private async Task<AuthorizationDecision> PersistDecisionAsync(
        string decisionId,
        AuthorizationRequest request,
        string amount,
        DateTimeOffset now,
        bool approved,
        string? reasonCode,
        string? mandateId,
        CancellationToken cancellationToken,
        DateTimeOffset? grantExpiresAt = null)
    {
        var decision = new AuthorizationDecision
        {
            DecisionId = decisionId,
            RequestedBy = request.RequestedBy,
            Operation = request.Operation,
            DebitAccountId = request.DebitAccountId,
            CreditAccountId = request.CreditAccountId,
            Amount = amount,
            Currency = request.Currency,
            IdempotencyKey = request.IdempotencyKey,
            MandateId = mandateId,
            Approved = approved,
            DenialReason = reasonCode,
            CreatedAt = now,
            GrantExpiresAt = grantExpiresAt,
        };
        dbContext.AuthorizationDecisions.Add(decision);

        // Never log/audit the signed grant token itself (DESIGN.md Section 6.2, decision #13) - only the
        // decision id and outcome ever appear here.
        auditLogger.Record(new AuditEvent
        {
            Action = approved ? AuditActions.AuthorizationApproved : AuditActions.AuthorizationDenied,
            EntityType = nameof(AuthorizationDecision),
            EntityId = decisionId,
            Status = approved ? "Approved" : "Denied",
            Details = approved ? $"mandateId: {mandateId}" : $"reason: {reasonCode}",
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return decision;
    }

    private static AuthorizationDecisionResponse ToDecisionResponse(AuthorizationDecision decision) => new(
        decision.DecisionId,
        decision.Approved,
        decision.DenialReason,
        decision.RequestedBy,
        decision.Operation,
        decision.DebitAccountId,
        decision.CreditAccountId,
        decision.Amount,
        decision.Currency,
        decision.MandateId,
        decision.CreatedAt);
}
