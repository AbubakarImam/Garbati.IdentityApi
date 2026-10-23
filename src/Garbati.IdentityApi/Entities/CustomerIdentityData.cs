namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.1 / 7.1: sensitive identity fields separated from Customer's ordinary operational
// fields, so they can carry stricter access control and, eventually, encryption-at-rest and a retention
// policy of their own.
//
// T-016 deliberately left Bvn/Nin out, pending a cryptography decision (see the original comment,
// preserved in git history). T-044 (garbati-platform D-26, 23 Oct 2026) made that decision - "store
// unencrypted for now, revisit encryption later" - after research found no specific NDPA/CBN mandate
// requiring encryption, only a general risk-based duty. Bvn/Nin below are therefore plain, unencrypted
// nullable strings, set once each via PATCH /v1/customers/{customerId}/identity-numbers
// (SubmitIdentityNumbersAsync) - never through this endpoint's sibling, UpdateDetailsAsync, which has
// ordinary "overwrite freely" semantics that are deliberately wrong for an identity number. Logging rules
// (ARCHITECTURE.md Section 5, DESIGN.md Section 6.2: "never log a raw BVN/NIN") apply to these two
// columns' values with no exception - including in AuditEvent.Details, which is not a Serilog log line
// but is just as durable and just as readable.
public class CustomerIdentityData
{
    public long Id { get; set; }
    public long CustomerId { get; set; }
    public string FullName { get; set; } = null!;
    public string? Email { get; set; }
    public string? PhoneNumber { get; set; }
    public string? Bvn { get; set; }
    public string? Nin { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Customer Customer { get; set; } = null!;
}
