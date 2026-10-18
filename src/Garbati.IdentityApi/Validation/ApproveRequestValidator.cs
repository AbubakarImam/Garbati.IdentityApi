using Garbati.IdentityApi.Contracts.Requests;

namespace Garbati.IdentityApi.Validation;

public class ApproveRequestValidator
{
    public IReadOnlyList<string> Validate(ApproveRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.CustomerId))
        {
            errors.Add("CustomerId is required.");
        }

        return errors;
    }
}
