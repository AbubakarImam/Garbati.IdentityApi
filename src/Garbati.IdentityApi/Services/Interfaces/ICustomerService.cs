using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;

namespace Garbati.IdentityApi.Services;

public interface ICustomerService
{
    Task<CustomerResponse> CreateCustomerAsync(CreateCustomerRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse?> GetCustomerAsync(string customerId, CancellationToken cancellationToken = default);

    Task<CustomerResponse> RecordKycDecisionAsync(string customerId, KycDecisionRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse> SuspendCustomerAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse> ReactivateCustomerAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse> CloseCustomerAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse> MarkDeceasedAsync(string customerId, CustomerStatusChangeRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse> UpdateDetailsAsync(string customerId, UpdateCustomerDetailsRequest request, CancellationToken cancellationToken = default);

    Task<CustomerResponse> SubmitIdentityNumbersAsync(string customerId, SubmitIdentityNumbersRequest request, CancellationToken cancellationToken = default);
}
