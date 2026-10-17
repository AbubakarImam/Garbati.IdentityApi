namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.2 / decision #19. Links one customer to one ledger account. v2 allows more than
// one ACTIVE mandate per LedgerAccountId (one per holder, for joint accounts) - but at most one active
// mandate per (CustomerId, LedgerAccountId) pair, enforced by a real database partial unique index
// (IdentityDbContext), not application logic alone. T-017 only ever creates single-holder accounts (one
// mandate per LedgerAccountId); the constraint simply has to not get in the way of a second, different
// customer's own mandate on the same account later (T-018).
//
// MandateId (not Id) is the public identifier used in URLs and in the signed grant's "mandateId" claim -
// same CUS-/MND- style as CustomerIdGenerator, never the internal numeric Id.
public class Mandate
{
    public long Id { get; set; }
    public string MandateId { get; set; } = null!;
    public long CustomerId { get; set; }
    public string LedgerAccountId { get; set; } = null!;
    public MandateStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string CreatedBy { get; set; } = null!;
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }

    public Customer Customer { get; set; } = null!;
    public List<MandatePermission> Permissions { get; set; } = [];
}
