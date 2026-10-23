using Garbati.IdentityApi.Contracts.Requests;

namespace Garbati.IdentityApi.Validation;

// T-044. Mirrors UpdateCustomerDetailsRequestValidator's "at least one field supplied" shape exactly
// (same reasoning: a request with nothing to do is rejected here, not silently accepted as a no-op), plus
// the 11-digit format check (IdentityNumberRule) for whichever of Bvn/Nin is actually supplied. The
// set-once/409 rule itself is not a format concern - it depends on what is already stored, so it is
// enforced in CustomerService.SubmitIdentityNumbersAsync, not here.
public class SubmitIdentityNumbersRequestValidator
{
    public IReadOnlyList<string> Validate(SubmitIdentityNumbersRequest request)
    {
        var errors = new List<string>();

        if (request.Bvn is null && request.Nin is null)
        {
            errors.Add("At least one of Bvn or Nin must be supplied.");
            return errors;
        }

        if (request.Bvn is not null && IdentityNumberRule.Validate("Bvn", request.Bvn) is { } bvnError)
        {
            errors.Add(bvnError);
        }

        if (request.Nin is not null && IdentityNumberRule.Validate("Nin", request.Nin) is { } ninError)
        {
            errors.Add(ninError);
        }

        return errors;
    }
}
