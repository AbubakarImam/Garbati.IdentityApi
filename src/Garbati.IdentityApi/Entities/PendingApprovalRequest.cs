namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 3.8.2. Collects approvals toward a joint account's RequiredApprovals threshold
// before IdentityApi will act - either issue a signed debit grant (Kind = Debit) or change the account's
// MandateApprovalPolicy (Kind = ThresholdChange, 3.8.4). RequestId (not Id) is the public identifier
// every approval is bound to (decision #25) - same internal-long-Id/public-string-id split as every other
// entity in this service (Mandate.MandateId, Customer.CustomerId, AuthorizationDecision.DecisionId).
//
// Operation/DebitAccountId/CreditAccountId/Amount/Currency/IdempotencyKey are the canonical transaction
// fields (3.8.2) and are only ever populated for Kind = Debit - they mirror the fields that will
// eventually appear in the signed grant, fixed at creation time so a later approval cannot be tricked into
// approving a changed transaction. RequestedRequiredApprovals is NOT one of the fields DESIGN.md 3.8.2
// enumerates; it is this task's own addition (flagged in the PR as a judgment call, not a guess at a
// ratified decision) to hold the desired new threshold for a ThresholdChange request - 3.8.4 says
// RequiredApprovals itself is forced to the account's current active-holder count (the number of
// approvals THIS request needs), which is a different number from the value being requested, and nothing
// in the design says where that second number lives.
public class PendingApprovalRequest
{
    public long Id { get; set; }
    public string RequestId { get; set; } = null!;
    public PendingApprovalRequestKind Kind { get; set; }
    public string LedgerAccountId { get; set; } = null!;
    public string RequestedBy { get; set; } = null!;

    public string? Operation { get; set; }
    public string? DebitAccountId { get; set; }
    public string? CreditAccountId { get; set; }
    public string? Amount { get; set; }
    public string? Currency { get; set; }
    public string? IdempotencyKey { get; set; }

    public int? RequestedRequiredApprovals { get; set; }

    // Copied from the account's policy (Debit) or its active-holder count (ThresholdChange) at creation
    // time - a concurrent policy change must not retroactively change what an in-flight request needs
    // (3.8.2).
    public int RequiredApprovals { get; set; }
    public PendingApprovalRequestStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }

    // Set once this request resolves to Approved for a Debit request - the AuthorizationDecision's
    // DecisionId (never the grant token itself, decision #13). Stays null for a ThresholdChange approval:
    // there is no decision/grant for a policy change (task item 4).
    public string? DecisionId { get; set; }

    public List<ApprovalRecord> Approvals { get; set; } = [];
}
