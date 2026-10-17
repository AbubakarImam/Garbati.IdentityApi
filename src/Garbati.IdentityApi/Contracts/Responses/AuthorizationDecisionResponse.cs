namespace Garbati.IdentityApi.Contracts.Responses;

// GET /v1/authorization/requests/{id} (DESIGN.md Section 5.1): "approved + the decision id, or denied +
// reason" - deliberately does NOT carry the signed grant token itself (never persisted, never re-served
// - Section 6.2, decision #13). See AuthorizationResponse for the one-time grant returned by the POST
// that created this decision.
public record AuthorizationDecisionResponse(
    string DecisionId,
    bool Approved,
    string? DenialReason,
    string RequestedBy,
    string Operation,
    string DebitAccountId,
    string? CreditAccountId,
    string Amount,
    string Currency,
    string? MandateId,
    DateTimeOffset CreatedAt);
