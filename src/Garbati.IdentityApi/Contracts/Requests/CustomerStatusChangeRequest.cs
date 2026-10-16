namespace Garbati.IdentityApi.Contracts.Requests;

// Shared shape for suspend/reactivate/close/mark-deceased (DESIGN.md Section 5.1, 2.1). Reason is
// required on all four: these are restricted workflows, and Section 6.3 expects their audit record to
// carry "actor and reason" - enforced by CustomerStatusChangeRequestValidator, not just claimed here in
// a comment (caught by QA review of the T-016 PR).
public record CustomerStatusChangeRequest(string? Reason);
