namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 5.1: POST /v1/authorization/requests/{id}/approve. CustomerId is the approving
// holder's public CustomerId - a different holder of the same account from RequestedBy on the
// PendingApprovalRequest being approved (DESIGN.md Section 3.8.1).
public record ApproveRequest(string CustomerId);
