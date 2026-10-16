namespace Garbati.IdentityApi.Exceptions;

public class DuplicateIdempotencyKeyException : Exception
{
    public DuplicateIdempotencyKeyException()
    {
    }

    public DuplicateIdempotencyKeyException(string message)
        : base(message)
    {
    }

    public DuplicateIdempotencyKeyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
