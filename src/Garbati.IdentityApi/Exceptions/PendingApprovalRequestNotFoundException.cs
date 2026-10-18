namespace Garbati.IdentityApi.Exceptions;

public class PendingApprovalRequestNotFoundException : Exception
{
    public PendingApprovalRequestNotFoundException()
    {
    }

    public PendingApprovalRequestNotFoundException(string message)
        : base(message)
    {
    }

    public PendingApprovalRequestNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
