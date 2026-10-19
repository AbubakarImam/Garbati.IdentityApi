namespace Garbati.IdentityApi.Exceptions;

// T-031 (garbati-platform): a pageToken that doesn't decode to the shape
// JointApprovalService.ListPendingRequestsAsync itself produces (base64 of a row's internal Id) is a
// client input error, not "page not found" - mapped to 400, the same exception-to-400 pattern
// InvalidApprovalThresholdException already uses for a check that needs data the controller alone
// cannot validate.
public class InvalidPageTokenException : Exception
{
    public InvalidPageTokenException()
    {
    }

    public InvalidPageTokenException(string message)
        : base(message)
    {
    }

    public InvalidPageTokenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
