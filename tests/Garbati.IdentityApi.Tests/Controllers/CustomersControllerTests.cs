using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Controllers;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Validation;
using Microsoft.AspNetCore.Mvc;

namespace Garbati.IdentityApi.Tests.Controllers;

public class CustomersControllerTests
{
    private static readonly CustomerResponse Customer = new(
        "CUS-ABCDEFGHJKLMNPQ", null, "Active", "Pending", null, "NG", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

    // Returns a fixed result (or null for "not found") and counts calls, so the controller's validation
    // and not-found mapping can be tested without a database (same approach as LedgerApi's
    // AccountsControllerTests' StubAccountService).
    private sealed class StubCustomerService(CustomerResponse? customer) : ICustomerService
    {
        public int CreateCalls { get; private set; }

        public Task<CustomerResponse> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            return Task.FromResult(customer!);
        }

        public Task<CustomerResponse?> GetCustomerAsync(string customerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer);

        public Task<CustomerResponse> RecordKycDecisionAsync(string customerId, KycDecisionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer!);

        public Task<CustomerResponse> SuspendCustomerAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer!);

        public Task<CustomerResponse> ReactivateCustomerAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer!);

        public Task<CustomerResponse> CloseCustomerAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer!);

        public Task<CustomerResponse> MarkDeceasedAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(customer!);
    }

    private static CustomersController CreateController(StubCustomerService service) =>
        new(service, new CreateCustomerRequestValidator(), new KycDecisionRequestValidator());

    [Fact]
    public async Task CreateCustomer_Returns200_WithTheCreatedCustomer()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.CreateCustomer(new CreateCustomerRequest("Ada Lovelace", "NG", null, null, null), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(Customer, ok.Value);
    }

    [Fact]
    public async Task CreateCustomer_Returns400_AndDoesNotCallTheService_WhenFullNameIsMissing()
    {
        var service = new StubCustomerService(Customer);
        var controller = CreateController(service);

        var result = await controller.CreateCustomer(new CreateCustomerRequest("", "NG", null, null, null), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var errors = Assert.IsAssignableFrom<IReadOnlyList<string>>(badRequest.Value);
        Assert.Contains(errors, e => e.Contains("FullName"));
        Assert.Equal(0, service.CreateCalls);
    }

    [Fact]
    public async Task GetCustomer_Returns200_WhenCustomerExists()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.GetCustomer("CUS-ABCDEFGHJKLMNPQ", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(Customer, ok.Value);
    }

    [Fact]
    public async Task GetCustomer_Returns404_WhenCustomerDoesNotExist()
    {
        var controller = CreateController(new StubCustomerService(null));

        var result = await controller.GetCustomer("CUS-DOESNOTEXIST", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task RecordKycDecision_Returns200_ForAValidDecision()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.RecordKycDecision(
            "CUS-ABCDEFGHJKLMNPQ",
            new KycDecisionRequest("Verified", "Tier1", "BVN", "test-provider", null, "OK", null),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(Customer, ok.Value);
    }

    [Fact]
    public async Task RecordKycDecision_Returns400_ForAnUnrecognizedStatus()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.RecordKycDecision(
            "CUS-ABCDEFGHJKLMNPQ",
            new KycDecisionRequest("NotARealStatus", null, "BVN", "test-provider", null, null, null),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task SuspendCustomer_Returns200()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.SuspendCustomer(
            "CUS-ABCDEFGHJKLMNPQ", new CustomerStatusChangeRequest("review"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task ReactivateCustomer_Returns200()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.ReactivateCustomer(
            "CUS-ABCDEFGHJKLMNPQ", new CustomerStatusChangeRequest(null), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task CloseCustomer_Returns200()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.CloseCustomer(
            "CUS-ABCDEFGHJKLMNPQ", new CustomerStatusChangeRequest("closure"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task MarkCustomerDeceased_Returns200()
    {
        var controller = CreateController(new StubCustomerService(Customer));

        var result = await controller.MarkCustomerDeceased(
            "CUS-ABCDEFGHJKLMNPQ", new CustomerStatusChangeRequest("deceased"), CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
    }
}
