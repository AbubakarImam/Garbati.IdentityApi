namespace Garbati.IdentityApi.Exceptions;

public class CustomerNotFoundException : Exception
{
    public CustomerNotFoundException()
    {
    }

    public CustomerNotFoundException(string message)
        : base(message)
    {
    }

    public CustomerNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
