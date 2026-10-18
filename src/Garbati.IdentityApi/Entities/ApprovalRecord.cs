namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 3.8.2. One row per holder's approval against a PendingApprovalRequest - including the
// requester's own, implicit approval, created at the same time as the request itself (decision #21: the
// initiator counts toward M). CustomerId is the approving holder's public CustomerId, not an internal FK -
// same convention AuthorizationDecision.RequestedBy already uses, since this row does not need
// cascade-FK integrity with Customers, only a readable, stable reference for audit/lookup.
//
// VoidedAt/VoidedReason are set the moment this holder's mandate is suspended or revoked while the
// request is still Pending (decision #24, "void immediately, not just on the next read") - never deleted,
// matching DESIGN.md 7.3's "ApprovalRecords are never deleted" rule.
//
// Unique constraint on (PendingApprovalRequestId, CustomerId) - enforced by the database
// (IdentityDbContext), not application code alone, matching every other uniqueness rule this service has
// built so far (decision #19's mandate constraint, decision #29's idempotency constraint).
public class ApprovalRecord
{
    public long Id { get; set; }
    public long PendingApprovalRequestId { get; set; }
    public string CustomerId { get; set; } = null!;
    public DateTimeOffset ApprovedAt { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public string? VoidedReason { get; set; }

    public PendingApprovalRequest PendingApprovalRequest { get; set; } = null!;
}
