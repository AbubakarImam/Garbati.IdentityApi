namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.1 / 7.1: sensitive identity fields separated from Customer's ordinary operational
// fields, so they can carry stricter access control and, eventually, encryption-at-rest and a retention
// policy of their own.
//
// Deliberately minimal for T-016 (not a full KYC-data-model build - see the task file): a name and basic
// contact details only. No BVN/NIN column here. DESIGN.md Section 6.2 forbids logging a raw BVN/NIN, and
// the same care has to apply to how one is *stored*, not just logged - e.g. encryption-at-rest via
// whatever standard mechanism the hosting Postgres offers, or column-level encryption. Neither has been
// decided yet, so rather than guess at cryptography this column is left out entirely. Flagged in the PR
// as a follow-up for whoever first needs to store a real BVN/NIN (likely T-017's KYC evidence work or a
// later task), not for this one to invent.
public class CustomerIdentityData
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string FullName { get; set; } = null!;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Customer Customer { get; set; } = null!;
}
