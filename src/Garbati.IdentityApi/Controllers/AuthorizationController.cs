using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Controllers;

// DESIGN.md Section 5.1. A single scope (identity.authorization.check) covers both actions here - the
// design explicitly says this scope "covers both POST /v1/authorization/requests and .../approve (no
// separate scope needed; both are the same customer-facing authorization concern)"; the GET lookup is
// the same concern read back.
[ApiController]
[Route("v1/authorization/requests")]
public class AuthorizationController(ITransactionAuthorizationService authorizationService, AuthorizationRequestValidator validator) : ControllerBase
{
    [Authorize(Policy = IdentityScopes.AuthorizationCheck)]
    [HttpPost]
    public async Task<ActionResult<AuthorizationResponse>> RequestAuthorization(
        [FromBody] AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var errors = validator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        // Denial is signalled by AuthorizationDeniedException (mapped to 422 by
        // ExceptionHandlingMiddleware), not by a 200 response with Approved = false - so there is no
        // response shape here that could be mistaken for a grant when none was issued.
        var response = await authorizationService.RequestAuthorizationAsync(request, cancellationToken);
        return Ok(response);
    }

    [Authorize(Policy = IdentityScopes.AuthorizationCheck)]
    [HttpGet("{id}")]
    public async Task<ActionResult<AuthorizationDecisionResponse>> GetAuthorizationRequest(
        string id, CancellationToken cancellationToken)
    {
        var decision = await authorizationService.GetDecisionAsync(id, cancellationToken);
        return decision is null ? NotFound() : Ok(decision);
    }
}
