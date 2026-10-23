using System.Text.RegularExpressions;

namespace Garbati.IdentityApi.Validation;

// T-044: both BVN and NIN are 11-digit numeric strings in Nigeria - a stable, well-known identifier
// format, not a legal nuance needing further compliance confirmation (see the task file). One rule,
// shared by both fields and named by the caller so the error message says which one failed. A null value
// is never checked here - "not supplied" and "supplied but malformed" are different concerns, handled by
// the caller (SubmitIdentityNumbersRequestValidator) before this rule ever runs.
public static partial class IdentityNumberRule
{
    public const int Length = 11;

    // [0-9], not \d: \d also matches non-ASCII Unicode decimal digits (e.g. Arabic-Indic), and the task
    // explicitly scopes this to ASCII digits only.
    [GeneratedRegex(@"^[0-9]{11}\z")]
    private static partial Regex Pattern();

    public static string? Validate(string fieldName, string value) =>
        Pattern().IsMatch(value) ? null : $"{fieldName} must be exactly 11 digits.";
}
