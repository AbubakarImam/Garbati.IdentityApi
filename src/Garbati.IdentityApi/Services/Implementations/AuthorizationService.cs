using System.Globalization;
using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Garbati.IdentityApi.Signing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Garbati.IdentityApi.Services;

// DESIGN.md Section 3 (3.1-3.7 - Section 3.8's joint-account collect-approvals workflow is
// JointApprovalService, T-018). RequestAuthorizationAsync itself still only ever has to satisfy M = 1:
// JointApprovalService checks the account's MandateApprovalPolicy BEFORE calling anything here, and only
// ever reaches this method when RequiredApprovals <= 1 (see AuthorizationDenialReasons.
// ApprovalPolicyNotSingleHolder below - that check exists so the one assumption the rest of this method
// depends on is verified, not assumed, even though nothing should ever call it otherwise now).
//
// IssueApprovedGrantAsync and RecordDenialAsync (T-018) are the sign-and-persist / deny-and-persist
// halves of this same logic, extracted so JointApprovalService can reuse them once a
// PendingApprovalRequest's threshold is met (3.8.1 phase 2), instead of duplicating them.
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

        // T-018: extracted to MandateEligibility.CheckAsync (the customer/KYC/mandate steps below are
        // byte-for-byte what used to be inline here) so the joint-approval flow can reuse the exact same
        // logic without duplicating it - identical reason codes, identical order, identical behavior.
        var (reasonCode, mandate) = await MandateEligibility.CheckAsync(
            dbContext, request.RequestedBy, request.DebitAccountId, operation, now, cancellationToken);

        if (reasonCode is null)
        {
            // Defensive, not decorative (T-017's task file): this is the one assumption this method's own
            // single-holder flow depends on. T-018's JointApprovalService is the one place that branches
            // on RequiredApprovals > 1 - it only ever calls THIS method once it has already confirmed
            // RequiredApprovals <= 1 itself, so this check staying exactly as it was is defence in depth,
            // not dead code.
            var policy = await dbContext.MandateApprovalPolicies.AsNoTracking()
                .FirstOrDefaultAsync(p => p.LedgerAccountId == request.DebitAccountId, cancellationToken);

            if (policy is null || policy.RequiredApprovals != 1)
            {
                reasonCode = AuthorizationDenialReasons.ApprovalPolicyNotSingleHolder;
            }
        }

        if (reasonCode is not null)
        {
            // T-049: use the entity PersistDecisionAsync actually returns, not the locally-generated
            // decisionId above - on a resolved duplicate (a retried denial), that local id was never
            // actually persisted, so a caller seeing it could never look it up via GetDecisionAsync.
            var decision = await PersistDecisionAsync(decisionId, request, amount, now, approved: false, reasonCode, mandateId: null, cancellationToken);
            throw new AuthorizationDeniedException(
                decision.DecisionId, decision.DenialReason!,
                $"Authorization denied for decision '{decision.DecisionId}': {decision.DenialReason}.");
        }

        // T-018: byte-for-byte what used to be inline here, moved into IssueApprovedGrantAsync so the
        // joint-approval flow can call the exact same sign-and-persist logic once a PendingApprovalRequest
        // reaches its threshold (3.8.1 phase 2), instead of duplicating it. `customer!.CustomerId` became
        // `request.RequestedBy` - the two are guaranteed equal whenever reasonCode is null (MandateEligibility
        // looked the customer up BY that exact CustomerId), so this is not a behavior change.
        return await IssueApprovedGrantAsync(decisionId, request, mandate!.MandateId, now, cancellationToken);
    }

    public async Task<AuthorizationDecisionResponse?> GetDecisionAsync(string decisionId, CancellationToken cancellationToken = default)
    {
        var decision = await dbContext.AuthorizationDecisions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DecisionId == decisionId, cancellationToken);
        return decision is null ? null : ToDecisionResponse(decision);
    }

    public async Task<AuthorizationResponse> IssueApprovedGrantAsync(
        string decisionId, AuthorizationRequest request, string mandateId, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var operation = Enum.Parse<MandateOperation>(request.Operation, ignoreCase: true);
        var amount = request.Amount.ToString("G29", CultureInfo.InvariantCulture);
        var expiresAt = now.AddSeconds(GrantLifetimeSeconds);
        var grant = grantSigner.Sign(new GrantClaims(
            Jti: decisionId,
            Sub: request.RequestedBy,
            Operation: operation.ToString(),
            DebitAccountId: request.DebitAccountId,
            // T-020 made AuthorizationRequest.CreditAccountId nullable (it has no meaning for a Reversal
            // request) - but this method is only ever reached for a non-Reversal MandateOperation (the
            // Enum.Parse above would throw for "Reversal"), and AuthorizationRequestValidator already
            // requires CreditAccountId for every one of those, so it is never actually null here.
            CreditAccountId: request.CreditAccountId!,
            Amount: amount,
            Currency: request.Currency,
            IdempotencyKey: request.IdempotencyKey,
            MandateId: mandateId,
            IssuedAt: now,
            ExpiresAt: expiresAt));

        var decision = await PersistDecisionAsync(
            decisionId, request, amount, now, approved: true, reasonCode: null, mandateId, cancellationToken, expiresAt);

        return new AuthorizationResponse(ToDecisionResponse(decision), grant.Token, expiresAt);
    }

    public Task RecordDenialAsync(
        string decisionId, AuthorizationRequest request, string reasonCode, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var amount = request.Amount.ToString("G29", CultureInfo.InvariantCulture);
        return PersistDecisionAsync(decisionId, request, amount, now, approved: false, reasonCode, mandateId: null, cancellationToken);
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
        var audit = auditLogger.Record(new AuditEvent
        {
            Action = approved ? AuditActions.AuthorizationApproved : AuditActions.AuthorizationDenied,
            EntityType = nameof(AuthorizationDecision),
            EntityId = decisionId,
            Status = approved ? "Approved" : "Denied",
            Details = approved ? $"mandateId: {mandateId}" : $"reason: {reasonCode}",
        });

        // T-049 (garbati-platform D-16): dedupe a RETRIED DENIAL, mirroring CustomerService.
        // CreateCustomerAsync's exact shape - attempt the insert directly (no separate "check first" read,
        // which would reintroduce the exact race the database constraint exists to close), catch the
        // unique-violation, resolve by querying, compare canonical request fields, 409 on a real mismatch.
        // An approved insert never hits the new index's filter (`WHERE approved = false`), so that case
        // always falls through to the plain `return decision;` below, unchanged.
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return decision;
        }
        catch (DbUpdateException ex) when (!approved
            && ex.InnerException is PostgresException { SqlState: "23505", ConstraintName:
                "ix_authorization_decisions_requested_by_idempotency_key_denied" })
        {
            // Detach everything this attempt staged, or the retry (the next call on this same dbContext,
            // if any) would try to save it a second time alongside later changes - same reason
            // CreateCustomerAsync detaches its own staged Customer/IdentityData/audit row.
            dbContext.Entry(decision).State = EntityState.Detached;
            dbContext.Entry(audit).State = EntityState.Detached;

            var existing = await dbContext.AuthorizationDecisions.AsNoTracking().FirstOrDefaultAsync(
                d => d.RequestedBy == request.RequestedBy && d.IdempotencyKey == request.IdempotencyKey
                    && !d.Approved, cancellationToken);

            if (existing is null)
                throw new InvalidOperationException("Error retrieving the result of the existing idempotency key.");

            // Same key, different request: refuse rather than report the old denial as this one's (409) -
            // compare the request's own canonical fields, not computed outputs like DenialReason.
            if (existing.Operation != request.Operation
                || existing.DebitAccountId != request.DebitAccountId
                || existing.CreditAccountId != request.CreditAccountId
                || existing.Amount != amount
                || existing.Currency != request.Currency)
            {
                throw new DuplicateIdempotencyKeyException(
                    $"Idempotency key '{request.IdempotencyKey}' was already used for a different request.");
            }

            return existing;
        }
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
        decision.CreatedAt,
        decision.ReasonCode);
}
