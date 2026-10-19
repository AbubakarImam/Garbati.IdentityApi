using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Controllers;

// DESIGN.md Section 5.1. A single scope (identity.authorization.check) covers both actions here - the
// design explicitly says this scope "covers both POST /v1/authorization/requests and .../approve (no
// separate scope needed; both are the same customer-facing authorization concern)"; the GET lookup is
// the same concern read back.
//
// T-020 (DESIGN.md Section 8): RequestAuthorization now also accepts Operation == "Reversal", gated by a
// DIFFERENT scope (identity.ops.reversal, decision #30) than every other operation this same endpoint
// accepts. That is a deliberate, documented exception to this controller's usual one-attribute-one-scope
// shape - see this action's own comment and ControllerAuthorizationTests' matching exception for why a
// static [Authorize(Policy=...)] attribute cannot express a scope choice that depends on the request BODY.
[ApiController]
[Route("v1/authorization/requests")]
public class AuthorizationController(
    ITransactionAuthorizationService authorizationService,
    IJointApprovalService jointApprovalService,
    IReversalAuthorizationService reversalAuthorizationService,
    AuthorizationRequestValidator validator,
    ApproveRequestValidator approveValidator,
    RecordReversalOutcomeRequestValidator reversalOutcomeValidator) : ControllerBase
{
    // T-031 (garbati-platform): the task file's own suggested default/max - nothing in this platform has
    // needed different values yet. Validated here (not in JointApprovalService) because neither check
    // needs the database - same reasoning as every other validator on this controller living outside the
    // service it feeds.
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    // T-018: branches on MandateApprovalPolicy.RequiredApprovals (DESIGN.md Section 5.1). The RequiredApprovals
    // <= 1 case is unchanged from T-017 - JointApprovalService.RequestDebitAsync delegates straight to
    // ITransactionAuthorizationService.RequestAuthorizationAsync for that branch, not reimplemented here.
    //
    // T-020: a Reversal request is gated by identity.ops.reversal, not identity.authorization.check -
    // decision #30/8.1's whole design is that an ops caller may hold ONLY identity.ops.reversal (the
    // dev-ops seed client, for example, never gets identity.authorization.check). ASP.NET Core evaluates
    // [Authorize(Policy=...)] before the action even runs, let alone before the body is bound, so a single
    // static attribute cannot pick between two scopes based on Operation's value - hence the explicit
    // User.HasClaim check below, after the validator has already confirmed Operation is well-formed. This
    // produces the exact same 403 ProblemDetails shape as a failed [Authorize] (ControllerBase.Forbid()
    // triggers the same ApiKeyAuthenticationHandler.HandleForbiddenAsync), so no new error shape exists.
    [Authorize]
    [HttpPost]
    public async Task<ActionResult> RequestAuthorization(
        [FromBody] AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var errors = validator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var isReversal = AuthorizationRequestValidator.IsReversal(request.Operation);
        var requiredScope = isReversal ? IdentityScopes.OpsReversal : IdentityScopes.AuthorizationCheck;
        if (!User.HasClaim(IdentityScopes.ClaimType, requiredScope))
        {
            return Forbid();
        }

        if (isReversal)
        {
            var reversalResponse = await reversalAuthorizationService.RequestReversalAsync(request, cancellationToken);
            return Ok(reversalResponse);
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

    // DESIGN.md Section 8.6: a single scope (identity.ops.reversal, same as the request itself) - unlike
    // RequestAuthorization above, this action never serves any other operation, so the ordinary
    // one-attribute-one-scope shape applies unchanged here.
    [Authorize(Policy = IdentityScopes.OpsReversal)]
    [HttpPost("{id}/reversal-outcome")]
    public async Task<ActionResult<ReversalOutcomeResponse>> RecordReversalOutcome(
        string id, [FromBody] RecordReversalOutcomeRequest request, CancellationToken cancellationToken)
    {
        var errors = reversalOutcomeValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var response = await reversalAuthorizationService.RecordOutcomeAsync(id, request, cancellationToken);
        return Ok(response);
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

    // T-031 (garbati-platform, D-15): a browsable queue of pending (or otherwise-statused) joint-approval
    // requests, e.g. for an ops-console "approvals queue" screen (OpsApi/T-028, not built yet). Same scope
    // as the GET-by-id action above - this is the same customer-facing authorization concern, read back as
    // a list instead of by one id. Purely additive: does not touch RequestAuthorization, Approve,
    // RecordReversalOutcome or GetAuthorizationRequest above.
    //
    // Deliberately scoped to PendingApprovalRequest only (JointApprovalService.ListPendingRequestsAsync's
    // own comment) - a single-holder (RequiredApprovals <= 1) request never creates one of these rows at
    // all, it resolves synchronously via AuthorizationDecision (see RequestAuthorization/RequestDebitAsync
    // above), so it never appears here. This is "pending joint-approval requests," not "every
    // authorization decision ever made."
    [Authorize(Policy = IdentityScopes.AuthorizationCheck)]
    [HttpGet]
    public async Task<ActionResult> ListPendingRequests(
        [FromQuery] string? status, [FromQuery] int? pageSize, [FromQuery] string? pageToken, CancellationToken cancellationToken)
    {
        var effectiveStatus = status ?? nameof(PendingApprovalRequestStatus.Pending);
        if (!Enum.TryParse<PendingApprovalRequestStatus>(effectiveStatus, ignoreCase: true, out var parsedStatus))
        {
            return BadRequest($"'{status}' is not a recognized status.");
        }

        var effectivePageSize = pageSize ?? DefaultPageSize;
        if (effectivePageSize < 1 || effectivePageSize > MaxPageSize)
        {
            return BadRequest($"pageSize must be between 1 and {MaxPageSize}.");
        }

        var response = await jointApprovalService.ListPendingRequestsAsync(
            parsedStatus, effectivePageSize, pageToken, cancellationToken);
        return Ok(response);
    }
}
