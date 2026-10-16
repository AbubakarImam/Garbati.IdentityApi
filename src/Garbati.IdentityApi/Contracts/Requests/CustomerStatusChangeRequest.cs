namespace Garbati.IdentityApi.Contracts.Requests;

// Shared shape for suspend/reactivate/close/mark-deceased (DESIGN.md Section 5.1, 2.1). Reason is
// required for suspend/close/mark-deceased (restricted workflows that must be explainable - Section 6.3)
// and optional for reactivate (DESIGN.md gives reactivate no explicit reason requirement).
public record CustomerStatusChangeRequest(string? Reason);
