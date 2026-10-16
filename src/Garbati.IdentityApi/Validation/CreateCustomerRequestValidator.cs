using Garbati.IdentityApi.Contracts.Requests;

namespace Garbati.IdentityApi.Validation;

public class CreateCustomerRequestValidator
{
    public IReadOnlyList<string> Validate(CreateCustomerRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.FullName))
        {
            errors.Add("FullName is required.");
        }

        if (string.IsNullOrWhiteSpace(request.JurisdictionCode))
        {
            errors.Add("JurisdictionCode is required.");
        }
        else if (request.JurisdictionCode.Length is < 2 or > 10)
        {
            errors.Add("JurisdictionCode must be between 2 and 10 characters.");
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && !request.Email.Contains('@'))
        {
            errors.Add("Email is not a valid email address.");
        }

        return errors;
    }
}
