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
public class AuthorizationController(
    ITransactionAuthorizationService authorizationService,
    IJointApprovalService jointApprovalService,
    AuthorizationRequestValidator validator,
    ApproveRequestValidator approveValidator) : ControllerBase
{
    // T-018: branches on MandateApprovalPolicy.RequiredApprovals (DESIGN.md Section 5.1). The RequiredApprovals
    // <= 1 case is unchanged from T-017 - JointApprovalService.RequestDebitAsync delegates straight to
    // ITransactionAuthorizationService.RequestAuthorizationAsync for that branch, not reimplemented here.
    [Authorize(Policy = IdentityScopes.AuthorizationCheck)]
    [HttpPost]
    public async Task<ActionResult> RequestAuthorization(
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
        var result = await jointApprovalService.RequestDebitAsync(request, cancellationToken);
        return result switch
        {
            DebitRequestResult.Granted granted => Ok(granted.Response),
            DebitRequestResult.Pending pending => Ok(pending.Response),
            _ => throw new InvalidOperationException($"Unhandled {nameof(DebitRequestResult)} case."),
        };
    }

    [Authorize(Policy = IdentityScopes.AuthorizationCheck)]
    [HttpPost("{id}/approve")]
    public async Task<ActionResult<PendingApprovalRequestResponse>> Approve(
        string id, [FromBody] ApproveRequest request, CancellationToken cancellationToken)
    {
        var errors = approveValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var response = await jointApprovalService.ApproveAsync(id, request, cancellationToken);
        return Ok(response);
    }

    // DESIGN.md Section 5.1: "status of a pending (or resolved) approval request." Falls back to the
    // T-018 PendingApprovalRequest lookup only when {id} does not resolve as an existing
    // AuthorizationDecision - the single-holder GetDecisionAsync path (T-017) is tried first and stays
    // completely unchanged.
    [Authorize(Policy = IdentityScopes.AuthorizationCheck)]
    [HttpGet("{id}")]
    public async Task<ActionResult> GetAuthorizationRequest(string id, CancellationToken cancellationToken)
    {
        var decision = await authorizationService.GetDecisionAsync(id, cancellationToken);
        if (decision is not null)
        {
            return Ok(decision);
        }

        var pending = await jointApprovalService.GetPendingRequestAsync(id, cancellationToken);
        return pending is null ? NotFound() : Ok(pending);
    }
}
