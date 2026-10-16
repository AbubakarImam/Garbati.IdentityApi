namespace Garbati.IdentityApi.Authorization;

// The API client making the current request (the client id from its API key), for services that are not
// controllers. Null outside an authenticated request, e.g. in service tests or background work.
public interface ICurrentClient
{
    string? ClientId { get; }
}

public class HttpCurrentClient(IHttpContextAccessor httpContextAccessor) : ICurrentClient
{
    public string? ClientId => httpContextAccessor.HttpContext?.User.Identity?.Name;
}
