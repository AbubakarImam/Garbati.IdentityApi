using Garbati.IdentityApi.Contracts.Requests;

namespace Garbati.IdentityApi.Validation;

// Guards suspend/reactivate/close/mark-deceased: all four are restricted workflows whose audit record
// must carry a reason (DESIGN.md Section 6.3), so a missing or blank one is rejected here rather than
// silently accepted (caught by QA review of the T-016 PR).
public class CustomerStatusChangeRequestValidator
{
    public IReadOnlyList<string> Validate(CustomerStatusChangeRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors.Add("Reason is required.");
        }

        return errors;
    }
}
