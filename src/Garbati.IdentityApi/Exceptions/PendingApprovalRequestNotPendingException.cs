namespace Garbati.IdentityApi.Exceptions;

// Thrown by the approve endpoint when the request is Expired/Approved/Cancelled (DESIGN.md Section
// 3.8.3: "a holder attempting to approve an Expired request gets a clear rejection"). Mapped to 409 -
// the same "conflict with current state" convention as InvalidCustomerStatusTransitionException.
public class PendingApprovalRequestNotPendingException : Exception
{
    public PendingApprovalRequestNotPendingException()
    {
    }

    public PendingApprovalRequestNotPendingException(string message)
        : base(message)
    {
    }

    public PendingApprovalRequestNotPendingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
