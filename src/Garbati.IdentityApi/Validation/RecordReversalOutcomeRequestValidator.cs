using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Validation;

public class RecordReversalOutcomeRequestValidator
{
    public IReadOnlyList<string> Validate(RecordReversalOutcomeRequest request)
    {
        var errors = new List<string>();

        if (!Enum.TryParse<ReversalOutcomeKind>(request.Outcome, ignoreCase: true, out _))
        {
            errors.Add($"'{request.Outcome}' is not a recognized Outcome.");
        }

        if (string.IsNullOrWhiteSpace(request.RecordedBy))
        {
            errors.Add("RecordedBy is required.");
        }

        return errors;
    }
}
