namespace Garbati.IdentityApi.Exceptions;

// T-020: POST /v1/authorization/requests/{id}/reversal-outcome needs a plain "no such decision at all"
// exception - no existing exception in this codebase covers it (GET /v1/authorization/requests/{id}
// handles a missing decision by returning null from the controller, not by throwing). Mapped to 404.
public class AuthorizationDecisionNotFoundException : Exception
{
    public AuthorizationDecisionNotFoundException()
    {
    }

    public AuthorizationDecisionNotFoundException(string message)
        : base(message)
    {
    }

    public AuthorizationDecisionNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
