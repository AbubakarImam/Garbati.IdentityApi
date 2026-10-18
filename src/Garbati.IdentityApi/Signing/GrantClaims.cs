namespace Garbati.IdentityApi.Signing;

// The exact set of claims the task file calls for (DESIGN.md Sections 3.3/3.5): iss/aud/typ are fixed by
// GrantSigner itself, everything transaction-specific is carried here. Amount must already be in
// canonical G29 string form (AuthorizationService) before it reaches this type - GrantSigner does not
// reformat it, so there is exactly one place in the codebase that decides the canonical representation.
//
// T-020: MandateId is now nullable - a Reversal grant has no mandate behind it at all (decision #31, the
// ops override bypasses the mandate model entirely), so there is nothing truthful to put there. ReasonCode
// is a new, optional trailing claim (decision #33: "the reason code as an added claim"), null for every
// operation except Reversal. GrantSigner omits both claims from the token entirely when null, rather than
// encoding a null/placeholder value, so every pre-T-020 grant's claim set is completely unchanged.
public record GrantClaims(
    string Jti,
    string Sub,
    string Operation,
    string DebitAccountId,
    string CreditAccountId,
    string Amount,
    string Currency,
    string IdempotencyKey,
    string? MandateId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string? ReasonCode = null);

public record SignedGrant(string Token, string Kid, DateTimeOffset ExpiresAt);
