using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Controllers;

// DESIGN.md Section 5.1 (v2, new): "the view a channel needs to show 'who else is on this account'."
[ApiController]
[Route("v1/accounts")]
public class AccountsController(
    IMandateService mandateService,
    IJointApprovalService jointApprovalService,
    ChangeApprovalPolicyRequestValidator changeApprovalPolicyRequestValidator) : ControllerBase
{
    [Authorize(Policy = IdentityScopes.MandateRead)]
    [HttpGet("{ledgerAccountId}/mandates")]
    public async Task<ActionResult<IReadOnlyList<MandateResponse>>> ListMandatesForAccount(
        string ledgerAccountId, CancellationToken cancellationToken)
    {
        var mandates = await mandateService.ListActiveHoldersForAccountAsync(ledgerAccountId, cancellationToken);
        return Ok(mandates);
    }

    // T-018, DESIGN.md Section 5.1 (v2, new). Judgment call (flagged in the PR): Section 5.2 does not
    // name a scope for this endpoint. MandateRead was chosen because this is a read of mandate/account
    // configuration, the same concern GET .../mandates above already covers under that scope.
    [Authorize(Policy = IdentityScopes.MandateRead)]
    [HttpGet("{ledgerAccountId}/approval-policy")]
    public async Task<ActionResult<ApprovalPolicyResponse>> GetApprovalPolicy(
        string ledgerAccountId, CancellationToken cancellationToken)
    {
        var policy = await jointApprovalService.GetApprovalPolicyAsync(ledgerAccountId, cancellationToken);
        return policy is null ? NotFound() : Ok(policy);
    }

    // T-018, DESIGN.md Section 5.1/3.8.4 (v2, new). Judgment call (flagged in the PR): Section 5.2 does
    // not name a scope for this endpoint either. AuthorizationCheck was chosen because this opens a
    // PendingApprovalRequest exactly like POST /v1/authorization/requests does - "the same customer-facing
    // authorization concern" Section 5.2 already uses to justify that scope covering both of ITS endpoints.
    [Authorize(Policy = IdentityScopes.AuthorizationCheck)]
    [HttpPost("{ledgerAccountId}/approval-policy/change-requests")]
    public async Task<ActionResult<PendingApprovalRequestResponse>> RequestApprovalPolicyChange(
        string ledgerAccountId, [FromBody] ChangeApprovalPolicyRequest request, CancellationToken cancellationToken)
    {
        var errors = changeApprovalPolicyRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var response = await jointApprovalService.RequestThresholdChangeAsync(ledgerAccountId, request, cancellationToken);
        return Ok(response);
    }
}
