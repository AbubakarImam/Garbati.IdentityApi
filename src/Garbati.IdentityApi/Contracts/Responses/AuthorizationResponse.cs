namespace Garbati.IdentityApi.Contracts.Responses;

// POST /v1/authorization/requests' response. Grant is the compact, signed JWS string - present only on
// approval, returned exactly once (never logged, never persisted - DESIGN.md Section 6.2, decision #13).
// A denial never reaches this type; AuthorizationService throws AuthorizationDeniedException instead, so
// there is no code path where Grant could accidentally be populated on a denial.
public record AuthorizationResponse(
    AuthorizationDecisionResponse Decision,
    string Grant,
    DateTimeOffset ExpiresAt);
