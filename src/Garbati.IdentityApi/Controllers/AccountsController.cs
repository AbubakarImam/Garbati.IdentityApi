using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Controllers;

// DESIGN.md Section 5.1 (v2, new): "the view a channel needs to show 'who else is on this account'."
[ApiController]
[Route("v1/accounts")]
public class AccountsController(IMandateService mandateService) : ControllerBase
{
    [Authorize(Policy = IdentityScopes.MandateRead)]
    [HttpGet("{ledgerAccountId}/mandates")]
    public async Task<ActionResult<IReadOnlyList<MandateResponse>>> ListMandatesForAccount(
        string ledgerAccountId, CancellationToken cancellationToken)
    {
        var mandates = await mandateService.ListActiveHoldersForAccountAsync(ledgerAccountId, cancellationToken);
        return Ok(mandates);
    }
}
