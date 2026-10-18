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

// DESIGN.md Section 8 (decisions #30-#35), garbati-platform T-020. Reversal authorization is a
// deliberately simpler sibling of AuthorizationService, NOT a variant of JointApprovalService:
//
// - No PendingApprovalRequest/ApprovalRecord-style workflow (decision #30 exists specifically so this
//   isn't needed - the ops channel's own maker-checker process, which does not exist yet, is the real
//   gate; by the time AuthorizationController routes a request here, the caller has already proven it
//   holds identity.ops.reversal).
// - No MandateEligibility check of any kind - no customer-status check, no KYC check, no mandate lookup,
//   no MandateApprovalPolicy.RequiredApprovals check (decision #31's "ops override"). This is the one
//   rule this class must never violate: if a future change adds a mandate/customer lookup here, that is
//   exactly the mistake decision #31 forbids.
// - The grant is signed through the exact same IGrantSigner.Sign/GrantClaims shape every other operation
//   uses (decision #33) and persisted as an ordinary AuthorizationDecision (same table, same shape,
//   "exactly as for any other request") - but NOT through AuthorizationService.IssueApprovedGrantAsync,
//   because that method parses request.Operation as a MandateOperation (Enum.Parse<MandateOperation>),
//   which "Reversal" is not and must never become (see AuthorizationRequestValidator's own comment on
//   why Reversal is deliberately kept out of that enum).
public class ReversalAuthorizationService(
    IdentityDbContext dbContext, IAuditLogger auditLogger, IGrantSigner grantSigner)
    : IReversalAuthorizationService
{
    private const int GrantLifetimeSeconds = 30; // same 30-second window as every other grant (decision #33).
    private const string ReversalOperation = "Reversal";

    public async Task<AuthorizationResponse> RequestReversalAsync(
        AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var decisionId = Guid.NewGuid().ToString();
        var amount = request.Amount.ToString("G29", CultureInfo.InvariantCulture);
        var expiresAt = now.AddSeconds(GrantLifetimeSeconds);

        // DESIGN.md Section 8.3: "a reference to the original transaction/chain being reversed, replacing
        // debit/credit accounts for this operation type." Judgment call (see AuthorizationRequest's own
        // comment for the full reasoning): DebitAccountId carries that single reference; CreditAccountId
        // has no meaning for a reversal, so when the caller leaves it out the grant's own creditAccountId
        // claim falls back to the same reference rather than a required grant field going null/blank.
        // IdentityApi does not independently verify this reference exists at LedgerApi - there is nothing
        // to call yet (DESIGN.md Section 8.3; same documented gap as the LedgerAccountId TODO at mandate
        // attachment, Section 2.4/MandateService.AttachMandateAsync - accepted as the caller's assertion,
        // not faked as a real check).
        var originalTransactionReference = request.DebitAccountId;
        var creditAccountId = request.CreditAccountId ?? originalTransactionReference;

        // QA fix: AuthorizationRequestValidator accepts any casing (Enum.TryParse(..., ignoreCase: true))
        // so "dupl"/"Dupl"/"DUPL" are all valid input, but Section 8.4 intends a fixed vocabulary - a
        // future consumer doing an exact string match against "DUPL" must not silently miss a validly
        // accepted lowercase variant. Normalize to the enum's own canonical name once, here, before it is
        // either persisted or placed in the grant claim - every other use of ReasonCode in this method
        // reads from this normalized value, never request.ReasonCode directly. Validator already
        // guarantees this parses for a Reversal request, so a parse failure here would mean the validator
        // itself has a bug, not a legitimate "no reason code" case.
        var reasonCode = Enum.Parse<ReversalReasonCode>(request.ReasonCode!, ignoreCase: true).ToString();

        var grant = grantSigner.Sign(new GrantClaims(
            Jti: decisionId,
            Sub: request.RequestedBy,
            Operation: ReversalOperation,
            DebitAccountId: originalTransactionReference,
            CreditAccountId: creditAccountId,
            Amount: amount,
            Currency: request.Currency,
            IdempotencyKey: request.IdempotencyKey,
            MandateId: null, // decision #31: no mandate backs a reversal authorization.
            IssuedAt: now,
            ExpiresAt: expiresAt,
            ReasonCode: reasonCode));

        var decision = new AuthorizationDecision
        {
            DecisionId = decisionId,
            RequestedBy = request.RequestedBy,
            Operation = ReversalOperation,
            DebitAccountId = originalTransactionReference,
            CreditAccountId = creditAccountId,
            Amount = amount,
            Currency = request.Currency,
            IdempotencyKey = request.IdempotencyKey,
            MandateId = null,
            ReasonCode = reasonCode,
            Approved = true,
            DenialReason = null,
            CreatedAt = now,
            GrantExpiresAt = expiresAt,
        };
        dbContext.AuthorizationDecisions.Add(decision);

        // Never log/audit the signed grant token itself (DESIGN.md Section 6.2, decision #13) - same rule,
        // same shape, as AuthorizationService.PersistDecisionAsync. One audit event covers "requested" and
        // "granted" together (there is no separate request/decide phase to split, decision #30/#31 - this
        // is a single synchronous call, not a PendingApprovalRequest lifecycle).
        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.AuthorizationApproved,
            EntityType = nameof(AuthorizationDecision),
            EntityId = decisionId,
            Status = "Approved",
            Details = $"operation: Reversal; reasonCode: {reasonCode}; reference: {originalTransactionReference}",
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        var response = new AuthorizationDecisionResponse(
            decision.DecisionId, decision.Approved, decision.DenialReason, decision.RequestedBy,
            decision.Operation, decision.DebitAccountId, decision.CreditAccountId, decision.Amount,
            decision.Currency, decision.MandateId, decision.CreatedAt, decision.ReasonCode);

        return new AuthorizationResponse(response, grant.Token, expiresAt);
    }

    public async Task<ReversalOutcomeResponse> RecordOutcomeAsync(
        string decisionId, RecordReversalOutcomeRequest request, CancellationToken cancellationToken = default)
    {
        var decision = await dbContext.AuthorizationDecisions.AsNoTracking()
            .FirstOrDefaultAsync(d => d.DecisionId == decisionId, cancellationToken)
            ?? throw new AuthorizationDecisionNotFoundException($"Authorization decision '{decisionId}' was not found.");

        // DESIGN.md Section 8.6: an outcome can only be recorded against a decision that was actually an
        // approved Reversal - not a denial, and not some other operation's decision id.
        if (!string.Equals(decision.Operation, ReversalOperation, StringComparison.OrdinalIgnoreCase) || !decision.Approved)
        {
            throw new ReversalOutcomeNotEligibleException(
                $"Authorization decision '{decisionId}' is not an approved Reversal decision.");
        }

        // Checked explicitly for a clean error message before relying on the unique database index as the
        // backstop (same "check first, constraint as backstop" pattern as
        // JointApprovalService.ApproveAsync's DuplicateApprovalException).
        var alreadyRecorded = await dbContext.ReversalOutcomes.AsNoTracking()
            .AnyAsync(o => o.AuthorizationDecisionId == decisionId, cancellationToken);
        if (alreadyRecorded)
        {
            throw new DuplicateReversalOutcomeException(
                $"Authorization decision '{decisionId}' already has a recorded reversal outcome.");
        }

        var now = DateTimeOffset.UtcNow;
        var outcome = new ReversalOutcome
        {
            AuthorizationDecisionId = decisionId,
            Outcome = Enum.Parse<ReversalOutcomeKind>(request.Outcome, ignoreCase: true),
            RecordedAt = now,
            RecordedBy = request.RecordedBy,
            Notes = request.Notes,
        };
        dbContext.ReversalOutcomes.Add(outcome);

        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.ReversalOutcomeRecorded,
            EntityType = nameof(ReversalOutcome),
            EntityId = decisionId,
            Status = outcome.Outcome.ToString(),
            Details = $"RecordedBy {request.RecordedBy}",
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: "23505", ConstraintName: "ix_reversal_outcomes_authorization_decision_id" })
        {
            // Concurrent double-submit backstop - the unique index is the real guard under a race; the
            // explicit check above only covers the common, non-racing case with a clean error (same
            // "check first, constraint as backstop" pattern as JointApprovalService.ApproveAsync).
            throw new DuplicateReversalOutcomeException(
                $"Authorization decision '{decisionId}' already has a recorded reversal outcome.");
        }

        return new ReversalOutcomeResponse(decisionId, outcome.Outcome.ToString(), outcome.RecordedAt, outcome.RecordedBy, outcome.Notes);
    }
}
