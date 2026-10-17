using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Validation;

// DESIGN.md Section 3.8.2's canonical fields. IdempotencyKey is required and format-checked (reusing
// IdempotencyKeyRule) because it is one of the fields bound into the signed grant (Section 3.3) - not
// because this endpoint enforces duplicate-request detection (task file: it deliberately does not).
public class AuthorizationRequestValidator
{
    public IReadOnlyList<string> Validate(AuthorizationRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.RequestedBy))
        {
            errors.Add("RequestedBy is required.");
        }

        if (!Enum.TryParse<MandateOperation>(request.Operation, ignoreCase: true, out _))
        {
            errors.Add($"'{request.Operation}' is not a recognized Operation.");
        }

        if (string.IsNullOrWhiteSpace(request.DebitAccountId))
        {
            errors.Add("DebitAccountId is required.");
        }

        if (string.IsNullOrWhiteSpace(request.CreditAccountId))
        {
            errors.Add("CreditAccountId is required.");
        }

        if (request.Amount <= 0)
        {
            errors.Add("Amount must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency) || request.Currency.Length != 3)
        {
            errors.Add("Currency must be a 3-letter currency code.");
        }

        if (IdempotencyKeyRule.Validate(request.IdempotencyKey) is { } error)
        {
            errors.Add(error);
        }

        return errors;
    }
}
