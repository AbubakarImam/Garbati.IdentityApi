using Garbati.IdentityApi.Authorization;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Controllers;

// DESIGN.md Section 5.1. Route literals (not [controller]) match the versioned paths the design spells
// out exactly (ARCHITECTURE.md Section 5: "/v1/" in the path).
[ApiController]
[Route("v1/customers")]
public class CustomersController(
    ICustomerService customerService,
    IMandateService mandateService,
    CreateCustomerRequestValidator createCustomerRequestValidator,
    KycDecisionRequestValidator kycDecisionRequestValidator,
    CustomerStatusChangeRequestValidator customerStatusChangeRequestValidator,
    AttachMandateRequestValidator attachMandateRequestValidator) : ControllerBase
{
    [Authorize(Policy = IdentityScopes.CustomerWrite)]
    [HttpPost]
    public async Task<ActionResult<CustomerResponse>> CreateCustomer(
        [FromBody] CreateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        var errors = createCustomerRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var customer = await customerService.CreateCustomerAsync(request, cancellationToken);
        return Ok(customer);
    }

    // Redacted view (DESIGN.md Section 6.2): never raw CustomerIdentityData fields.
    [Authorize(Policy = IdentityScopes.CustomerRead)]
    [HttpGet("{customerId}")]
    public async Task<ActionResult<CustomerResponse>> GetCustomer(string customerId, CancellationToken cancellationToken)
    {
        var customer = await customerService.GetCustomerAsync(customerId, cancellationToken);
        return customer is null ? NotFound() : Ok(customer);
    }

    [Authorize(Policy = IdentityScopes.KycManage)]
    [HttpPatch("{customerId}/kyc")]
    public async Task<ActionResult<CustomerResponse>> RecordKycDecision(
        string customerId,
        [FromBody] KycDecisionRequest request,
        CancellationToken cancellationToken)
    {
        var errors = kycDecisionRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var customer = await customerService.RecordKycDecisionAsync(customerId, request, cancellationToken);
        return Ok(customer);
    }

    [Authorize(Policy = IdentityScopes.CustomerWrite)]
    [HttpPost("{customerId}/suspend")]
    public async Task<ActionResult<CustomerResponse>> SuspendCustomer(
        string customerId,
        [FromBody] CustomerStatusChangeRequest request,
        CancellationToken cancellationToken)
    {
        var errors = customerStatusChangeRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var customer = await customerService.SuspendCustomerAsync(customerId, request, cancellationToken);
        return Ok(customer);
    }

    [Authorize(Policy = IdentityScopes.CustomerWrite)]
    [HttpPost("{customerId}/reactivate")]
    public async Task<ActionResult<CustomerResponse>> ReactivateCustomer(
        string customerId,
        [FromBody] CustomerStatusChangeRequest request,
        CancellationToken cancellationToken)
    {
        var errors = customerStatusChangeRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var customer = await customerService.ReactivateCustomerAsync(customerId, request, cancellationToken);
        return Ok(customer);
    }

    // DESIGN.md Section 5.1 lists only suspend/reactivate; close and mark-deceased are this task's own
    // addition (see the PR description for why) - Section 2.1's status table treats Closed and Deceased
    // as distinct, terminal, irreversible states, which "suspend" does not capture.
    [Authorize(Policy = IdentityScopes.CustomerWrite)]
    [HttpPost("{customerId}/close")]
    public async Task<ActionResult<CustomerResponse>> CloseCustomer(
        string customerId,
        [FromBody] CustomerStatusChangeRequest request,
        CancellationToken cancellationToken)
    {
        var errors = customerStatusChangeRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var customer = await customerService.CloseCustomerAsync(customerId, request, cancellationToken);
        return Ok(customer);
    }

    [Authorize(Policy = IdentityScopes.CustomerWrite)]
    [HttpPost("{customerId}/mark-deceased")]
    public async Task<ActionResult<CustomerResponse>> MarkCustomerDeceased(
        string customerId,
        [FromBody] CustomerStatusChangeRequest request,
        CancellationToken cancellationToken)
    {
        var errors = customerStatusChangeRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var customer = await customerService.MarkDeceasedAsync(customerId, request, cancellationToken);
        return Ok(customer);
    }

    // DESIGN.md Section 5.1/2.4 (T-017): "may be called more than once per account, once per holder, for
    // a joint account" - this task only ever exercises the single-holder case, but the endpoint and the
    // database constraint both already allow more.
    [Authorize(Policy = IdentityScopes.MandateWrite)]
    [HttpPost("{customerId}/mandates")]
    public async Task<ActionResult<MandateResponse>> AttachMandate(
        string customerId,
        [FromBody] AttachMandateRequest request,
        CancellationToken cancellationToken)
    {
        var errors = attachMandateRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return BadRequest(errors);
        }

        var mandate = await mandateService.AttachMandateAsync(customerId, request, cancellationToken);
        return Ok(mandate);
    }

    [Authorize(Policy = IdentityScopes.MandateRead)]
    [HttpGet("{customerId}/mandates")]
    public async Task<ActionResult<IReadOnlyList<MandateResponse>>> ListMandatesForCustomer(
        string customerId, CancellationToken cancellationToken)
    {
        var mandates = await mandateService.ListMandatesForCustomerAsync(customerId, cancellationToken);
        return Ok(mandates);
    }
}
