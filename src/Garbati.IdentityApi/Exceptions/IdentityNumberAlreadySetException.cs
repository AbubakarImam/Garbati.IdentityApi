namespace Garbati.IdentityApi.Exceptions;

// T-044: thrown by CustomerService.SubmitIdentityNumbersAsync when the stored Bvn/Nin value is already
// set to something different from what the request supplies. Mapped to 409 - the same "a real, provable
// state conflict" convention DuplicateActiveMandateException already uses, not a 422 business-rule
// failure, because nothing about eligibility is being judged here; the stored value simply cannot be
// changed via this endpoint (no admin-override path exists in this task - see the task file).
public class IdentityNumberAlreadySetException : Exception
{
    public IdentityNumberAlreadySetException()
    {
    }

    public IdentityNumberAlreadySetException(string message)
        : base(message)
    {
    }

    public IdentityNumberAlreadySetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
