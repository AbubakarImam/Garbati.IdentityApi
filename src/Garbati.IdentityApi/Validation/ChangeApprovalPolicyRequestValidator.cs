using Garbati.IdentityApi.Contracts.Requests;

namespace Garbati.IdentityApi.Validation;

// Only shape validation here (RequestedBy present, RequiredApprovals a positive integer). The
// account-state-dependent bound - "and no more than the account's current active-holder count"
// (DESIGN.md Section 3.8.4) - needs a database lookup the account doesn't have in this synchronous
// validator, so it is enforced by the service instead (InvalidApprovalThresholdException).
public class ChangeApprovalPolicyRequestValidator
{
    public IReadOnlyList<string> Validate(ChangeApprovalPolicyRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.RequestedBy))
        {
            errors.Add("RequestedBy is required.");
        }

        if (request.RequiredApprovals < 1)
        {
            errors.Add("RequiredApprovals must be at least 1.");
        }

        return errors;
    }
}
