namespace Garbati.IdentityApi.Contracts.Responses;

// DESIGN.md Section 5.1. Operations is returned as strings (MandateOperation names), not raw enum ints,
// so a caller never has to know the C# enum's underlying values.
public record MandateResponse(
    string MandateId,
    string CustomerId,
    string LedgerAccountId,
    string Status,
    IReadOnlyList<string> Operations,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    string? RevocationReason);
