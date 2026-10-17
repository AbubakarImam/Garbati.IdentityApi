namespace Garbati.IdentityApi.Exceptions;

// DESIGN.md Section 3.1/4.2/5.1: "a clear reason code, no grant, no exception leaking a stack trace."
// Mapped to 422 ("business-rule failure", ARCHITECTURE.md Section 5) by ExceptionHandlingMiddleware,
// which also adds decisionId/reasonCode to the ProblemDetails response - the decision was already
// persisted (AuthorizationService.PersistDecisionAsync) before this is thrown, so the id in the response
// always resolves via GET /v1/authorization/requests/{id}.
public class AuthorizationDeniedException(string decisionId, string reasonCode, string message) : Exception(message)
{
    public string DecisionId { get; } = decisionId;
    public string ReasonCode { get; } = reasonCode;
}
