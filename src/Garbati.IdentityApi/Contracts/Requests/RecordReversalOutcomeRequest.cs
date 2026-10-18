namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 8.6: POST /v1/authorization/requests/{id}/reversal-outcome's body. Outcome must be
// "Completed" or "WriteOff" (ReversalOutcomeKind) - validated by RecordReversalOutcomeRequestValidator.
public record RecordReversalOutcomeRequest(string Outcome, string RecordedBy, string? Notes);
