using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;

namespace Garbati.IdentityApi.Services;

public interface IMandateService
{
    Task<MandateResponse> AttachMandateAsync(
        string customerId, AttachMandateRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MandateResponse>> ListMandatesForCustomerAsync(
        string customerId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MandateResponse>> ListActiveHoldersForAccountAsync(
        string ledgerAccountId, CancellationToken cancellationToken = default);

    Task<MandateResponse?> GetMandateAsync(string mandateId, CancellationToken cancellationToken = default);

    Task<MandateResponse> RevokeMandateAsync(
        string mandateId, RevokeMandateRequest request, CancellationToken cancellationToken = default);
}
