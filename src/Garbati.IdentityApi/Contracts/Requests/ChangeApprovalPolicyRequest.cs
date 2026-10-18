namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 5.1/3.8.4: POST /v1/accounts/{ledgerAccountId}/approval-policy/change-requests.
// RequiredApprovals here is the DESIRED new threshold - not the number of approvals this specific
// request needs to pass (that is forced to the account's current active-holder count, decision #22).
public record ChangeApprovalPolicyRequest(string RequestedBy, int RequiredApprovals);
