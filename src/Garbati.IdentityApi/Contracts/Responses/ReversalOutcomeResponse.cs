namespace Garbati.IdentityApi.Contracts.Responses;

public record ReversalOutcomeResponse(
    string AuthorizationDecisionId,
    string Outcome,
    DateTimeOffset RecordedAt,
    string RecordedBy,
    string? Notes);
