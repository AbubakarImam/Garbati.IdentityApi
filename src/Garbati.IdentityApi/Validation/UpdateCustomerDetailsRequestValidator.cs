using Garbati.IdentityApi.Contracts.Requests;

namespace Garbati.IdentityApi.Validation;

// T-035: guards PATCH /v1/customers/{customerId}/details. A request with every field null has nothing to
// update, so it is rejected here rather than silently succeeding as a no-op. Field-level checks mirror
// CreateCustomerRequestValidator exactly for these same three fields - this is the same data (created
// there, edited here), so the rigor should not differ just because the operation does: Email must contain
// '@' if supplied and non-blank (identical check, copied verbatim); PhoneNumber has no format check at
// all, because CreateCustomerRequestValidator does not apply one either - inventing a stricter rule here
// than creation already enforces is exactly what the task file warns against.
//
// QA review of this PR: on a PATCH, null and "" are not the same instruction - null means "leave this
// field alone," "" is the caller actively supplying a value. FullName being supplied-but-blank was
// already rejected, but Email/PhoneNumber were not - "" silently passed every check below and would have
// overwritten the stored value with a blank string. Fixed: any of the three fields that is present
// (non-null) but blank (empty or all-whitespace) is rejected the same way, consistently, rather than
// leaving two of three fields able to blank out a customer's stored contact details by accident.
public class UpdateCustomerDetailsRequestValidator
{
    public IReadOnlyList<string> Validate(UpdateCustomerDetailsRequest request)
    {
        var errors = new List<string>();

        if (request.FullName is null && request.Email is null && request.PhoneNumber is null)
        {
            errors.Add("At least one of FullName, Email or PhoneNumber must be supplied.");
            return errors;
        }

        if (request.FullName is not null && string.IsNullOrWhiteSpace(request.FullName))
        {
            errors.Add("FullName cannot be blank.");
        }

        if (request.Email is not null && string.IsNullOrWhiteSpace(request.Email))
        {
            errors.Add("Email cannot be blank.");
        }
        else if (!string.IsNullOrWhiteSpace(request.Email) && !request.Email.Contains('@'))
        {
            errors.Add("Email is not a valid email address.");
        }

        if (request.PhoneNumber is not null && string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            errors.Add("PhoneNumber cannot be blank.");
        }

        return errors;
    }
}
