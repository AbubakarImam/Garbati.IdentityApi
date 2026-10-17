using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;

namespace Garbati.IdentityApi.Services;

// Named ITransactionAuthorizationService, not IAuthorizationService, to avoid colliding with ASP.NET
// Core's own Microsoft.AspNetCore.Authorization.IAuthorizationService (an unrelated type this project
// also references via [Authorize] policies).
public interface ITransactionAuthorizationService
{
    // Throws AuthorizationDeniedException on denial - never returns a "denied" AuthorizationResponse,
    // so there is no code path where a caller could mistakenly treat a denial as a grant.
    Task<AuthorizationResponse> RequestAuthorizationAsync(
        AuthorizationRequest request, CancellationToken cancellationToken = default);

    Task<AuthorizationDecisionResponse?> GetDecisionAsync(string decisionId, CancellationToken cancellationToken = default);
}
