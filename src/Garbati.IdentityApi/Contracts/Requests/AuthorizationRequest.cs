namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 3.8.2's canonical fields (even though the joint-account PendingApprovalRequest
// entity itself isn't built here - see the task file). RequestedBy is the requesting customer's public
// CustomerId. No idempotency *enforcement* happens on this field here (decision left open in the task
// file, see the PR description) - IdempotencyKey is still required input because it is one of the
// canonical fields bound into the signed grant (Section 3.3).
public record AuthorizationRequest(
    string RequestedBy,
    string Operation,
    string DebitAccountId,
    string CreditAccountId,
    decimal Amount,
    string Currency,
    string IdempotencyKey);
