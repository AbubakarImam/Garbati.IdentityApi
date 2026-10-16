namespace Garbati.IdentityApi.Exceptions;

// Thrown for any status change that the customer's current status does not allow - including a repeat
// of an already-terminal transition (e.g. closing an already-Closed customer). Mapped to 409: the task
// file leaves "409 or no-op" as a choice for a repeated Closed/Deceased attempt; 409 was chosen to match
// LedgerApi's own convention for a repeated terminal operation (AlreadyReversedException -> 409), and so
// a second call that carries a different reason is never silently dropped.
public class InvalidCustomerStatusTransitionException : Exception
{
    public InvalidCustomerStatusTransitionException()
    {
    }

    public InvalidCustomerStatusTransitionException(string message)
        : base(message)
    {
    }

    public InvalidCustomerStatusTransitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
