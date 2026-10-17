namespace Garbati.IdentityApi.Exceptions;

public class MandateNotFoundException : Exception
{
    public MandateNotFoundException()
    {
    }

    public MandateNotFoundException(string message)
        : base(message)
    {
    }

    public MandateNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
