using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Garbati.IdentityApi.Authorization;

// Authenticates the calling service from its X-Api-Key header. IdentityApi authorizes services, not
// customers directly (same split as LedgerApi decision #34/#35 - ARCHITECTURE.md Section 5); each key
// maps to a client id and the scopes it was granted.
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<ApiKeyOptions> apiKeyOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No header: "not authenticated" rather than "failed"; the endpoint's policy decides what that means.
        if (!Request.Headers.TryGetValue(HeaderName, out var presented) || string.IsNullOrEmpty(presented))
            return Task.FromResult(AuthenticateResult.NoResult());

        var presentedHash = ApiKeyHasher.Hash(presented.ToString());
        var client = apiKeyOptions.CurrentValue.Clients.FirstOrDefault(c =>
            ApiKeyHasher.TryDecode(c.KeyHash, out var configured)
            && CryptographicOperations.FixedTimeEquals(configured, presentedHash));

        if (client is null)
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        // Name = client id: this is what the request log's UserId and audit_events.actor_id pick up.
        var claims = new List<Claim> { new(ClaimTypes.Name, client.ClientId) };
        claims.AddRange(client.Scopes.Select(scope => new Claim(IdentityScopes.ClaimType, scope)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        return WriteProblemAsync(StatusCodes.Status401Unauthorized, "A valid API key is required.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteProblemAsync(StatusCodes.Status403Forbidden, "This API key is not allowed to perform this operation.");

    // Same ProblemDetails shape (with correlationId) as ExceptionHandlingMiddleware's errors.
    private Task WriteProblemAsync(int status, string title)
    {
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = status,
                Title = title,
                Extensions = { ["correlationId"] = Context.TraceIdentifier }
            },
            options: null,
            contentType: "application/problem+json");
    }
}
