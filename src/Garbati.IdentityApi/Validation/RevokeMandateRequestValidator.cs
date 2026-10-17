using Garbati.IdentityApi.Contracts.Requests;

namespace Garbati.IdentityApi.Validation;

// Mirrors CustomerStatusChangeRequestValidator (task file, Section 3): Reason is required, not just
// documented as required.
public class RevokeMandateRequestValidator
{
    public IReadOnlyList<string> Validate(RevokeMandateRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors.Add("Reason is required.");
        }

        return errors;
    }
}
