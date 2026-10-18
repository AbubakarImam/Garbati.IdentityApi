namespace Garbati.IdentityApi.Exceptions;

// DESIGN.md Section 3.8.4/5.1: a requested RequiredApprovals value outside [1, active-holder count] is
// rejected "with a clear 400" - the task file's own words. This check needs the account's current
// active-holder count from the database, so it cannot live in a stateless request validator the way
// ordinary shape checks do (ExceptionHandlingMiddleware's Map() normally reserves 400 for per-controller
// BadRequest - this is the one exception, because the task explicitly calls for 400 here, not 422).
public class InvalidApprovalThresholdException : Exception
{
    public InvalidApprovalThresholdException()
    {
    }

    public InvalidApprovalThresholdException(string message)
        : base(message)
    {
    }

    public InvalidApprovalThresholdException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
