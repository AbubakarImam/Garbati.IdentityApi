using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Controllers;

// DESIGN.md Section 5.1.
[ApiController]
[Route("v1/mandates")]
public class MandatesController(IMandateService mandateService, RevokeMandateRequestValidator revokeMandateRequestValidator) : ControllerBase
{
    [Authorize(Policy = IdentityScopes.MandateRead)]
    [HttpGet("{mandateId}")]
    public async Task<ActionResult<MandateResponse>> GetMandate(string mandateId, CancellationToken cancellationToken)
    {
        var mandate = await mandateService.GetMandateAsync(mandateId, cancellationToken);
        return mandate is null ? NotFound() : Ok(mandate);
    }

    [Authorize(Policy = IdentityScopes.MandateWrite)]
    [HttpPost("{mandateId}/revoke")]
    public async Task<ActionResult<MandateResponse>> RevokeMandate(
        string mandateId,
        [FromBody] RevokeMandateRequest request,
        CancellationToken cancellationToken)
    {
        var errors = revokeMandateRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var mandate = await mandateService.RevokeMandateAsync(mandateId, request, cancellationToken);
        return Ok(mandate);
    }
}
