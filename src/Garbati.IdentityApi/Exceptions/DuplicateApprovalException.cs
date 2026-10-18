namespace Garbati.IdentityApi.Exceptions;

// Thrown when a holder tries to approve a request they have already (non-voidedly) approved - the task
// file's "clean error message", checked explicitly before relying on the unique-constraint backstop
// (ix_approval_records_request_customer) for the same reason T-017's DuplicateActiveMandateException
// pairs an application check with its own database constraint. Mapped to 409.
public class DuplicateApprovalException : Exception
{
    public DuplicateApprovalException()
    {
    }

    public DuplicateApprovalException(string message)
        : base(message)
    {
    }

    public DuplicateApprovalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
