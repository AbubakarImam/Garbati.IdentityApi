using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;

namespace Garbati.IdentityApi.Validation;

// DESIGN.md Section 3.8.2's canonical fields. IdempotencyKey is required and format-checked (reusing
// IdempotencyKeyRule) because it is one of the fields bound into the signed grant (Section 3.3) - not
// because this endpoint enforces duplicate-request detection (task file: it deliberately does not).
//
// T-020: "Reversal" is deliberately NOT a MandateOperation value (DESIGN.md Section 2.2's enum describes
// what a MANDATE can permit - View/Debit/Credit - and a reversal never touches a mandate at all,
// decision #31). This validator accepts it as a second, separate kind of Operation string instead of
// adding it to that enum, which would wrongly imply a mandate could someday hold "Reversal" permission.
public class AuthorizationRequestValidator
{
    public IReadOnlyList<string> Validate(AuthorizationRequest request)
    {
        var errors = new List<string>();
        var isReversal = IsReversal(request.Operation);

        if (string.IsNullOrWhiteSpace(request.RequestedBy))
        {
            errors.Add("RequestedBy is required.");
        }

        if (!isReversal && !Enum.TryParse<MandateOperation>(request.Operation, ignoreCase: true, out _))
        {
            errors.Add($"'{request.Operation}' is not a recognized Operation.");
        }

        if (string.IsNullOrWhiteSpace(request.DebitAccountId))
        {
            // For a Reversal request this is the original transaction/chain reference (see
            // AuthorizationRequest's own comment), not a ledger account - but it is required either way.
            errors.Add("DebitAccountId is required.");
        }

        // CreditAccountId has no meaning for a Reversal (there is no destination account to name) -
        // T-017's #TBD-8 flagged exactly this as the case that would justify relaxing this check.
        if (!isReversal && string.IsNullOrWhiteSpace(request.CreditAccountId))
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

        // DESIGN.md Section 8.4, decision #32: exactly these five codes. DISPUTE is deliberately not a
        // member of ReversalReasonCode (see its own comment) - any attempt at it, or anything else, is
        // rejected here with 400, same as any other recognized-value check in this validator.
        if (isReversal && (string.IsNullOrWhiteSpace(request.ReasonCode)
            || !Enum.TryParse<ReversalReasonCode>(request.ReasonCode, ignoreCase: true, out _)))
        {
            errors.Add($"'{request.ReasonCode}' is not a recognized ReasonCode.");
        }

        return errors;
    }

    public static bool IsReversal(string operation) =>
        string.Equals(operation, "Reversal", StringComparison.OrdinalIgnoreCase);
}
