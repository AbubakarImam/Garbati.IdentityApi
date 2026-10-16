namespace Garbati.IdentityApi.Contracts.Responses;

// DESIGN.md Section 5.1/6.2: the redacted view. Deliberately excludes every CustomerIdentityData field
// (name, email, phone) - not only BVN/NIN, which this service does not even store yet (Entities/
// CustomerIdentityData.cs) - so nothing from that table can leak into a GET response by omission.
public record CustomerResponse(
    string CustomerId,
    string? CustomerReference,
    string CustomerStatus,
    string KycStatus,
    string? KycTier,
    string JurisdictionCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
