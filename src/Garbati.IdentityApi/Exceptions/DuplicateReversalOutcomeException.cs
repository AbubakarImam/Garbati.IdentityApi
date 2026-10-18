namespace Garbati.IdentityApi.Exceptions;

// DESIGN.md Section 8.6: "at most one outcome per decision (second attempt: 409, matching the established
// 'terminal state, don't silently overwrite' convention from Mandates/Customer's Closed)."
public class DuplicateReversalOutcomeException : Exception
{
    public DuplicateReversalOutcomeException()
    {
    }

    public DuplicateReversalOutcomeException(string message)
        : base(message)
    {
    }

    public DuplicateReversalOutcomeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
