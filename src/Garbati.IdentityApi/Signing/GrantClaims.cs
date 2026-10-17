namespace Garbati.IdentityApi.Signing;

// The exact set of claims the task file calls for (DESIGN.md Sections 3.3/3.5): iss/aud/typ are fixed by
// GrantSigner itself, everything transaction-specific is carried here. Amount must already be in
// canonical G29 string form (AuthorizationService) before it reaches this type - GrantSigner does not
// reformat it, so there is exactly one place in the codebase that decides the canonical representation.
public record GrantClaims(
    string Jti,
    string Sub,
    string Operation,
    string DebitAccountId,
    string CreditAccountId,
    string Amount,
    string Currency,
    string IdempotencyKey,
    string MandateId,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt);

public record SignedGrant(string Token, string Kid, DateTimeOffset ExpiresAt);
