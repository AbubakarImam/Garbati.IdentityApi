using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Validation;

public class KycDecisionRequestValidator
{
    public IReadOnlyList<string> Validate(KycDecisionRequest request)
    {
        var errors = new List<string>();

        if (!Enum.TryParse<KycStatus>(request.KycStatus, ignoreCase: true, out var status))
        {
            errors.Add($"'{request.KycStatus}' is not a recognized KYC status.");
        }
        else if (status == KycStatus.Verified && string.IsNullOrWhiteSpace(request.KycTier))
        {
            errors.Add("KycTier is required when KycStatus is Verified.");
        }

        if (string.IsNullOrWhiteSpace(request.VerificationType))
        {
            errors.Add("VerificationType is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Provider))
        {
            errors.Add("Provider is required.");
        }

        return errors;
    }
}
