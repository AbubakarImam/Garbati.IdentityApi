namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.1. CustomerId (decision #19/#16) is the internally generated, stable public
// identifier - never a government id, BVN, NIN or ledger account number. Sensitive identity fields live
// on CustomerIdentityData instead, kept separate from this operational record (Section 2.1, 7.1).
public class Customer
{
    public long Id { get; set; }
    public string CustomerId { get; set; } = null!;
    public string? CustomerReference { get; set; }
    public CustomerStatus CustomerStatus { get; set; }
    public KycStatus KycStatus { get; set; }
    public string? KycTier { get; set; }
    public string JurisdictionCode { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Idempotency for POST /v1/customers (decision #29, mirroring LedgerApi decision #43): the client's
    // key, who sent it, and a hash of the request that produced this row. A retry with the same key and
    // a matching hash returns this row; a different hash is rejected (409).
    public string IdempotencyKey { get; set; } = null!;
    public string? InitiatedBy { get; set; }
    public string RequestHash { get; set; } = null!;

    public CustomerIdentityData? IdentityData { get; set; }
}
