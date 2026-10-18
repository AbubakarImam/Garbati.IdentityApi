namespace Garbati.IdentityApi.Contracts.Responses;

// DESIGN.md Section 5.1/3.8. Operation/DebitAccountId/CreditAccountId/Amount/Currency are null for a
// ThresholdChange request (there is no transaction, only a policy change); RequestedRequiredApprovals is
// null for a Debit request (there is no threshold being changed). ApprovalCount is this task's own
// addition - not a DESIGN.md field - for a channel polling GET to show progress ("1 of 2") without
// counting Approvals itself.
//
// Grant is populated ONLY on the direct response to the exact POST .../approve call that completed the
// threshold and triggered grant issuance (DESIGN.md Section 3.8.1 phase 2) - never on a later GET, and
// never persisted (decision #13, same rule as AuthorizationResponse.Grant). DecisionId, in contrast, is
// durable and safe to re-serve on every later GET once set - it is only a reference, never the grant
// itself (DESIGN.md Section 5.1: "status and, if Approved, the decision id").
public record PendingApprovalRequestResponse(
    string RequestId,
    string Kind,
    string Status,
    string LedgerAccountId,
    string RequestedBy,
    string? Operation,
    string? DebitAccountId,
    string? CreditAccountId,
    string? Amount,
    string? Currency,
    int? RequestedRequiredApprovals,
    int RequiredApprovals,
    int ApprovalCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? DecisionId,
    string? Grant);
