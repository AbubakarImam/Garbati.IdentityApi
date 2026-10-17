namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 5.1: POST /v1/mandates/{mandateId}/revoke. Mirrors CustomerStatusChangeRequest -
// Reason is required (RevokeMandateRequestValidator), not just documented as required (same QA finding
// from T-016's PR applied here up front, per the task file).
public record RevokeMandateRequest(string? Reason);
