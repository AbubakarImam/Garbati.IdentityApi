using System.Globalization;
using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Garbati.IdentityApi.Services;

// DESIGN.md Section 3.8. This class owns the joint-account multi-approval workflow end to end: branching
// POST /v1/authorization/requests on RequiredApprovals (3.8.1), the .../approve endpoint (both Debit and
// ThresholdChange kinds, 3.8.1/3.8.4), the lazy expiry check (3.8.3), and the live re-check at
// threshold-completion time (3.8.5/decision #24). It calls into ITransactionAuthorizationService for the
// pieces that are genuinely unchanged from T-017 (the single-holder grant-issuance logic) rather than
// re-implementing them - see IssueApprovedGrantAsync/RecordDenialAsync's own comments.
public class JointApprovalService(
    IdentityDbContext dbContext, IAuditLogger auditLogger, ITransactionAuthorizationService authorizationService)
    : IJointApprovalService
{
    private static readonly TimeSpan PendingApprovalTimeout = TimeSpan.FromHours(1); // decision #23

    private static PendingApprovalRequestResponse ToResponse(PendingApprovalRequest pending, string? grant = null) => new(
        pending.RequestId,
        pending.Kind.ToString(),
        pending.Status.ToString(),
        pending.LedgerAccountId,
        pending.RequestedBy,
        pending.Operation,
        pending.DebitAccountId,
        pending.CreditAccountId,
        pending.Amount,
        pending.Currency,
        pending.RequestedRequiredApprovals,
        pending.RequiredApprovals,
        pending.Approvals.Count(a => a.VoidedAt is null),
        pending.CreatedAt,
        pending.ExpiresAt,
        pending.DecisionId,
        grant);

    // DESIGN.md Section 5.1/3.8.1: "For a single-holder account (RequiredApprovals = 1) this immediately
    // satisfies the threshold and behaves like v1's POST /v1/authorization/check" - i.e. exactly today's
    // AuthorizationService.RequestAuthorizationAsync, untouched, not re-implemented here.
    public async Task<DebitRequestResult> RequestDebitAsync(AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var policy = await dbContext.MandateApprovalPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LedgerAccountId == request.DebitAccountId, cancellationToken);

        if (policy is null || policy.RequiredApprovals <= 1)
        {
            return new DebitRequestResult.Granted(await authorizationService.RequestAuthorizationAsync(request, cancellationToken));
        }

        var operation = Enum.Parse<MandateOperation>(request.Operation, ignoreCase: true);

        // 3.8.1: the initiator must themselves be an eligible, mandated holder before a
        // PendingApprovalRequest is even opened - otherwise any CustomerId could open approval requests
        // against an account they have no standing on. Reuses the exact eligibility logic the
        // single-holder path applies (MandateEligibility), so a request that could never be granted is
        // never allowed to exist in the first place.
        var (reasonCode, _) = await MandateEligibility.CheckAsync(
            dbContext, request.RequestedBy, request.DebitAccountId, operation, now, cancellationToken);

        if (reasonCode is not null)
        {
            var decisionId = Guid.NewGuid().ToString();
            await authorizationService.RecordDenialAsync(decisionId, request, reasonCode, now, cancellationToken);
            throw new AuthorizationDeniedException(
                decisionId, reasonCode, $"Authorization denied for decision '{decisionId}': {reasonCode}.");
        }

        var pendingRequest = new PendingApprovalRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Kind = PendingApprovalRequestKind.Debit,
            LedgerAccountId = request.DebitAccountId,
            RequestedBy = request.RequestedBy,
            Operation = operation.ToString(),
            DebitAccountId = request.DebitAccountId,
            CreditAccountId = request.CreditAccountId,
            Amount = request.Amount.ToString("G29", CultureInfo.InvariantCulture),
            Currency = request.Currency,
            IdempotencyKey = request.IdempotencyKey,
            // Copied from the account's policy at creation time (3.8.2) - a concurrent policy change must
            // not retroactively change what THIS request needs.
            RequiredApprovals = policy.RequiredApprovals,
            Status = PendingApprovalRequestStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.Add(PendingApprovalTimeout),
        };
        // The requester's own approval, recorded at creation time (decision #21: the initiator counts
        // toward M).
        pendingRequest.Approvals.Add(new ApprovalRecord { CustomerId = request.RequestedBy, ApprovedAt = now });

        dbContext.PendingApprovalRequests.Add(pendingRequest);
        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.PendingApprovalRequestCreated,
            EntityType = nameof(PendingApprovalRequest),
            EntityId = pendingRequest.RequestId,
            Status = pendingRequest.Status.ToString(),
            Details = $"Kind Debit; LedgerAccountId {pendingRequest.LedgerAccountId}; RequiredApprovals {pendingRequest.RequiredApprovals}",
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        // A fresh multi-holder request can never be satisfied by the requester's own single approval
        // alone (RequiredApprovals > 1 is exactly the branch that got us here), so this is always Pending
        // immediately after creation - never "already complete."
        return new DebitRequestResult.Pending(ToResponse(pendingRequest));
    }

    public async Task<PendingApprovalRequestResponse> ApproveAsync(
        string requestId, ApproveRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        // QA-flagged concurrency fix: without a row lock, two different holders racing the final
        // approval of the same request can each independently load the pre-existing approval count,
        // each add their own, each conclude the threshold is met, and each run the completion logic -
        // two signed grants for a request that must resolve exactly once. SELECT ... FOR UPDATE locks
        // THIS request's row for the rest of this method, so a concurrent ApproveAsync call on the same
        // requestId blocks until this one commits (or rolls back) - the same "decide and act atomically
        // under concurrency" category of problem LedgerApi solves by locking accounts before posting
        // (ARCHITECTURE.md Section 12.5), applied here to one row instead of a set of ledger accounts.
        // A real database-enforced guard, not an application-level check: Postgres itself blocks the
        // second transaction, it is not this code "being careful."
        //
        // The FOR UPDATE row lock also blocks a concurrent INSERT of a new ApprovalRecord against this
        // same PendingApprovalRequestId before it can even start, not only the final status update:
        // inserting a child row with a foreign key to a locked parent row requires Postgres to take a
        // FOR KEY SHARE lock on that parent, which conflicts with (and so waits behind) the FOR UPDATE
        // lock already held here. So the whole "load approvals, add one, maybe complete" sequence below
        // is serialized per request, not just the completion step.
        //
        // Approvals are loaded as a second, separate statement (Collection(...).LoadAsync, not
        // Include() composed onto the locking query) because Postgres refuses FOR UPDATE on the
        // outer-joined side of the LEFT JOIN an Include() on a collection navigation would add
        // ("FOR UPDATE cannot be applied to the nullable side of an outer join") - no such join is
        // needed for that second statement to see every approval already committed against this row,
        // since nothing else can have inserted one without first winning the very lock held below.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var pending = await dbContext.PendingApprovalRequests
            .FromSqlInterpolated($"SELECT * FROM pending_approval_requests WHERE request_id = {requestId} FOR UPDATE")
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new PendingApprovalRequestNotFoundException($"Pending approval request '{requestId}' was not found.");

        await dbContext.Entry(pending).Collection(r => r.Approvals).LoadAsync(cancellationToken);

        await ExpireIfPastDueAsync(pending, now, cancellationToken);

        if (pending.Status != PendingApprovalRequestStatus.Pending)
        {
            // Committed before throwing (not left to the transaction's automatic rollback-on-dispose):
            // if ExpireIfPastDueAsync just wrote the Expired transition, that fact is real and must
            // persist regardless of why this particular approve call is then rejected. This is also
            // exactly the "clean conflict response" a holder who loses the race above receives: having
            // been unblocked by the winner's commit, they load a request that is now Approved, not
            // Pending, and this check reports that - no second grant, no silent overwrite.
            await transaction.CommitAsync(cancellationToken);
            throw new PendingApprovalRequestNotPendingException(
                $"Pending approval request '{requestId}' is {pending.Status}, not Pending.");
        }

        // Checked explicitly for a clean error message before relying on the unique constraint as the
        // backstop (same pattern as DuplicateActiveMandateException/T-017).
        if (pending.Approvals.Any(a => a.CustomerId == request.CustomerId && a.VoidedAt is null))
        {
            throw new DuplicateApprovalException(
                $"Customer '{request.CustomerId}' has already approved request '{requestId}'.");
        }

        var approverCustomer = await dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == request.CustomerId, cancellationToken)
            ?? throw new JointApprovalDeniedException(
                AuthorizationDenialReasons.CustomerNotFound, $"Customer '{request.CustomerId}' was not found.");

        var approverMandate = await dbContext.Mandates.AsNoTracking()
            .Include(m => m.Permissions)
            .FirstOrDefaultAsync(
                m => m.CustomerId == approverCustomer.Id && m.LedgerAccountId == pending.LedgerAccountId
                     && m.Status == MandateStatus.Active,
                cancellationToken)
            ?? throw new JointApprovalDeniedException(
                AuthorizationDenialReasons.NoActiveMandate,
                $"Customer '{request.CustomerId}' does not hold an active mandate on account '{pending.LedgerAccountId}'.");

        // DESIGN.md Section 3.8.1's approve validation list: "for a Debit kind, permission for the
        // operation." A ThresholdChange approval needs only an active mandate - consenting to a policy
        // change belongs to being a holder at all, not to any particular MandateOperation permission.
        if (pending.Kind == PendingApprovalRequestKind.Debit)
        {
            var operation = Enum.Parse<MandateOperation>(pending.Operation!, ignoreCase: true);
            if (!approverMandate.Permissions.Any(p => p.Operation == operation))
            {
                throw new JointApprovalDeniedException(
                    AuthorizationDenialReasons.OperationNotPermittedByMandate,
                    $"Customer '{request.CustomerId}'s mandate does not permit '{pending.Operation}' on account '{pending.LedgerAccountId}'.");
            }
        }

        pending.Approvals.Add(new ApprovalRecord
        {
            PendingApprovalRequestId = pending.Id,
            CustomerId = request.CustomerId,
            ApprovedAt = now,
        });

        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.ApprovalGiven,
            EntityType = nameof(PendingApprovalRequest),
            EntityId = pending.RequestId,
            Status = pending.Status.ToString(),
            Details = $"CustomerId {request.CustomerId}",
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: "23505", ConstraintName: "ix_approval_records_request_customer" })
        {
            // A concurrent double-submit from the same holder raced past the in-memory check above - the
            // database constraint is the backstop (task file), translated to the same clean error.
            throw new DuplicateApprovalException(
                $"Customer '{request.CustomerId}' has already approved request '{requestId}'.");
        }

        var validApprovals = pending.Approvals.Where(a => a.VoidedAt is null).ToList();
        if (validApprovals.Count < pending.RequiredApprovals)
        {
            await transaction.CommitAsync(cancellationToken);
            return ToResponse(pending);
        }

        // DESIGN.md Section 3.8.5/decision #24, the live half: at the exact instant the M-th approval
        // would complete the threshold, re-check every CONTRIBUTING approver's mandate is still Active
        // right now - not just at the moment each approval was individually given. This queries Mandates
        // directly, independent of whether MandateService's eager void (the other half of decision #24)
        // already caught a revoke - it is the second, defensive line, not a read of the first.
        var staleFound = false;
        foreach (var approval in validApprovals)
        {
            var approverForRecheck = await dbContext.Customers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CustomerId == approval.CustomerId, cancellationToken);
            var stillActive = approverForRecheck is not null && await dbContext.Mandates.AsNoTracking().AnyAsync(
                m => m.CustomerId == approverForRecheck.Id && m.LedgerAccountId == pending.LedgerAccountId
                     && m.Status == MandateStatus.Active,
                cancellationToken);

            if (!stillActive)
            {
                approval.VoidedAt = now;
                approval.VoidedReason = "Mandate no longer active at threshold-completion re-check.";
                auditLogger.Record(new AuditEvent
                {
                    Action = AuditActions.ApprovalVoided,
                    EntityType = nameof(PendingApprovalRequest),
                    EntityId = pending.RequestId,
                    Status = pending.Status.ToString(),
                    Details = $"CustomerId {approval.CustomerId}; reason: {approval.VoidedReason}",
                });
                staleFound = true;
            }
        }

        if (staleFound)
        {
            // Decision #24: a now-void approval at the exact moment of completion means the request stays
            // Pending, waiting for a replacement approval from a currently-active holder (or expiry) - the
            // grant/policy change is NOT issued on this call.
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResponse(pending);
        }

        var result = pending.Kind == PendingApprovalRequestKind.Debit
            ? await CompleteDebitRequestAsync(pending, now, cancellationToken)
            : await CompleteThresholdChangeRequestAsync(pending, now, cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // DESIGN.md Section 3.8.1 phase 2: "IdentityApi performs the ordinary single-holder authorization
    // check (3.2) using the requester's own eligibility as the basis, and issues the signed grant exactly
    // as before" - this is that check, run fresh (decision #9: never a cached/earlier result), plus
    // IssueApprovedGrantAsync/RecordDenialAsync (the same sign-and-persist code T-017 built, shared via
    // ITransactionAuthorizationService rather than duplicated).
    private async Task<PendingApprovalRequestResponse> CompleteDebitRequestAsync(
        PendingApprovalRequest pending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var operation = Enum.Parse<MandateOperation>(pending.Operation!, ignoreCase: true);
        var (reasonCode, requesterMandate) = await MandateEligibility.CheckAsync(
            dbContext, pending.RequestedBy, pending.LedgerAccountId, operation, now, cancellationToken);

        var requesterRequest = new AuthorizationRequest(
            pending.RequestedBy, pending.Operation!, pending.DebitAccountId!, pending.CreditAccountId!,
            decimal.Parse(pending.Amount!, CultureInfo.InvariantCulture), pending.Currency!, pending.IdempotencyKey!);

        if (reasonCode is not null)
        {
            // Judgment call, flagged in the PR: DESIGN.md's own PendingApprovalRequest.Status list
            // (Pending/Approved/Expired/Cancelled, 3.8.2) has no "Denied" state, and nothing in 3.8/4.2
            // says what happens if the REQUESTER's own eligibility lapses while waiting on other holders
            // (e.g. their mandate gets revoked, or KYC expires) - a fact that is not any approver's fault.
            // Rather than invent a status this design doesn't have, the denial is recorded for audit/
            // traceability (the same AuthorizationDecision mechanism the single-holder path already uses)
            // and the request simply stays Pending - the same observable outcome decision #24 already
            // specifies for a stale APPROVER, applied here to a stale REQUESTER instead. The approving
            // holder whose call triggered this still gets back a normal 200 Pending response: their own
            // approval WAS validly recorded, so their call did not fail.
            await authorizationService.RecordDenialAsync(Guid.NewGuid().ToString(), requesterRequest, reasonCode, now, cancellationToken);
            return ToResponse(pending);
        }

        var decisionId = Guid.NewGuid().ToString();
        // Set BEFORE calling IssueApprovedGrantAsync (not after) so that its own SaveChangesAsync - which
        // flushes every tracked change on this same dbContext, not only the AuthorizationDecision it adds
        // - commits the pending request's resolution and the grant decision together, in one database
        // transaction, rather than two.
        pending.Status = PendingApprovalRequestStatus.Approved;
        pending.DecisionId = decisionId;
        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.PendingApprovalRequestResolved,
            EntityType = nameof(PendingApprovalRequest),
            EntityId = pending.RequestId,
            Status = pending.Status.ToString(),
            Details = $"decisionId: {decisionId}",
        });

        var grantResponse = await authorizationService.IssueApprovedGrantAsync(
            decisionId, requesterRequest, requesterMandate!.MandateId, now, cancellationToken);

        return ToResponse(pending, grant: grantResponse.Grant);
    }

    // DESIGN.md Section 3.8.4: "no grant is issued for this kind - there's no money movement to
    // authorize, just a policy change." No requester-eligibility recheck either (unlike Debit) - there is
    // no KYC-tier/operation dimension to a policy change, and the task's own approve-validation list
    // (3.8.1) never asks for one here.
    private async Task<PendingApprovalRequestResponse> CompleteThresholdChangeRequestAsync(
        PendingApprovalRequest pending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var policy = await dbContext.MandateApprovalPolicies
            .FirstOrDefaultAsync(p => p.LedgerAccountId == pending.LedgerAccountId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"No MandateApprovalPolicy exists for account '{pending.LedgerAccountId}' - every account with a mandate has one (DESIGN.md Section 2.2a).");

        policy.RequiredApprovals = pending.RequestedRequiredApprovals!.Value;
        policy.LastChangedAt = now;
        // "A reference to the consent record that approved the change" (DESIGN.md Section 2.2a).
        policy.LastChangedBy = pending.RequestId;

        pending.Status = PendingApprovalRequestStatus.Approved;

        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.PendingApprovalRequestResolved,
            EntityType = nameof(PendingApprovalRequest),
            EntityId = pending.RequestId,
            Status = pending.Status.ToString(),
            Details = $"RequiredApprovals -> {policy.RequiredApprovals}",
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(pending);
    }

    public async Task<PendingApprovalRequestResponse?> GetPendingRequestAsync(
        string requestId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await dbContext.PendingApprovalRequests
            .Include(r => r.Approvals)
            .FirstOrDefaultAsync(r => r.RequestId == requestId, cancellationToken);

        if (pending is null)
        {
            return null;
        }

        await ExpireIfPastDueAsync(pending, now, cancellationToken);

        // GET never re-serves the grant token (decision #13) - only the AuthorizationDecision's id, once
        // set, which is just a reference.
        return ToResponse(pending, grant: null);
    }

    public async Task<ApprovalPolicyResponse?> GetApprovalPolicyAsync(
        string ledgerAccountId, CancellationToken cancellationToken = default)
    {
        var policy = await dbContext.MandateApprovalPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LedgerAccountId == ledgerAccountId, cancellationToken);

        if (policy is null)
        {
            return null;
        }

        var activeHolderCount = await dbContext.Mandates.CountAsync(
            m => m.LedgerAccountId == ledgerAccountId && m.Status == MandateStatus.Active, cancellationToken);

        return new ApprovalPolicyResponse(
            ledgerAccountId, policy.RequiredApprovals, activeHolderCount, policy.CreatedAt, policy.LastChangedAt, policy.LastChangedBy);
    }

    // DESIGN.md Section 3.8.4/decision #22: "every current holder must consent" - RequiredApprovals on
    // this PendingApprovalRequest is forced to the account's current active-holder count, not the
    // account's current threshold, closing the gap where today's-threshold's-worth of holders could
    // unilaterally weaken the account's protection without every holder's knowledge.
    public async Task<PendingApprovalRequestResponse> RequestThresholdChangeAsync(
        string ledgerAccountId, ChangeApprovalPolicyRequest request, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var policy = await dbContext.MandateApprovalPolicies.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LedgerAccountId == ledgerAccountId, cancellationToken)
            ?? throw new ApprovalPolicyNotFoundException($"No approval policy exists for account '{ledgerAccountId}'.");

        var activeHolderCount = await dbContext.Mandates.CountAsync(
            m => m.LedgerAccountId == ledgerAccountId && m.Status == MandateStatus.Active, cancellationToken);

        if (request.RequiredApprovals < 1 || request.RequiredApprovals > activeHolderCount)
        {
            throw new InvalidApprovalThresholdException(
                $"RequiredApprovals must be between 1 and {activeHolderCount} (account '{ledgerAccountId}'s current active-holder count), got {request.RequiredApprovals}.");
        }

        // Judgment call: the requester must themselves currently be an active holder. DESIGN.md 3.8.4
        // does not spell this out explicitly for request CREATION, only for approval - but RequiredApprovals
        // is forced to activeHolderCount and the requester's own approval is recorded as one of that count
        // (decision #21, applied here too), so if the requester were not a holder, "every holder" could be
        // satisfied by fewer than every holder, defeating decision #22's whole point.
        var requesterCustomer = await dbContext.Customers.AsNoTracking()
            .FirstOrDefaultAsync(c => c.CustomerId == request.RequestedBy, cancellationToken)
            ?? throw new JointApprovalDeniedException(
                AuthorizationDenialReasons.CustomerNotFound, $"Customer '{request.RequestedBy}' was not found.");

        var requesterIsHolder = await dbContext.Mandates.AsNoTracking().AnyAsync(
            m => m.CustomerId == requesterCustomer.Id && m.LedgerAccountId == ledgerAccountId && m.Status == MandateStatus.Active,
            cancellationToken);

        if (!requesterIsHolder)
        {
            throw new JointApprovalDeniedException(
                AuthorizationDenialReasons.NoActiveMandate,
                $"Customer '{request.RequestedBy}' does not hold an active mandate on account '{ledgerAccountId}'.");
        }

        var pendingRequest = new PendingApprovalRequest
        {
            RequestId = Guid.NewGuid().ToString(),
            Kind = PendingApprovalRequestKind.ThresholdChange,
            LedgerAccountId = ledgerAccountId,
            RequestedBy = request.RequestedBy,
            RequestedRequiredApprovals = request.RequiredApprovals,
            RequiredApprovals = activeHolderCount,
            Status = PendingApprovalRequestStatus.Pending,
            CreatedAt = now,
            ExpiresAt = now.Add(PendingApprovalTimeout),
        };
        pendingRequest.Approvals.Add(new ApprovalRecord { CustomerId = request.RequestedBy, ApprovedAt = now });

        dbContext.PendingApprovalRequests.Add(pendingRequest);
        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.PendingApprovalRequestCreated,
            EntityType = nameof(PendingApprovalRequest),
            EntityId = pendingRequest.RequestId,
            Status = pendingRequest.Status.ToString(),
            Details = $"Kind ThresholdChange; LedgerAccountId {ledgerAccountId}; " +
                      $"current {policy.RequiredApprovals} -> requested {request.RequiredApprovals}; needs all {activeHolderCount} holders",
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(pendingRequest);
    }

    // DESIGN.md Section 3.8.3/Section 5 (item 5 of the task file): checked lazily, whenever a request is
    // read or acted on - no scheduler exists anywhere in this codebase and this task does not add one.
    private async Task ExpireIfPastDueAsync(PendingApprovalRequest pending, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (pending.Status != PendingApprovalRequestStatus.Pending || pending.ExpiresAt > now)
        {
            return;
        }

        pending.Status = PendingApprovalRequestStatus.Expired;
        auditLogger.Record(new AuditEvent
        {
            Action = AuditActions.PendingApprovalRequestExpired,
            EntityType = nameof(PendingApprovalRequest),
            EntityId = pending.RequestId,
            Status = pending.Status.ToString(),
        });

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
