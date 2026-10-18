namespace Garbati.IdentityApi.Contracts.Responses;

// DESIGN.md Section 5.1: GET /v1/accounts/{ledgerAccountId}/approval-policy - "current RequiredApprovals
// and the account's active-holder count."
public record ApprovalPolicyResponse(
    string LedgerAccountId,
    int RequiredApprovals,
    int ActiveHolderCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastChangedAt,
    string? LastChangedBy);
