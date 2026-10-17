using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Validation;

public class AttachMandateRequestValidator
{
    public IReadOnlyList<string> Validate(AttachMandateRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.LedgerAccountId))
        {
            errors.Add("LedgerAccountId is required.");
        }

        if (request.Operations is null || request.Operations.Count == 0)
        {
            errors.Add("At least one Operation is required.");
        }
        else
        {
            foreach (var operation in request.Operations)
            {
                if (!Enum.TryParse<MandateOperation>(operation, ignoreCase: true, out _))
                {
                    errors.Add($"'{operation}' is not a recognized Operation.");
                }
            }
        }

        return errors;
    }
}
