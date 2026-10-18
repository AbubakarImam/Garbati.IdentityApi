namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 3.3/5.1/6.3: a durable record of one authorization decision (approved or denied),
// so GET /v1/authorization/requests/{id} has something to look up after the fact. This is NOT the
// T-018 PendingApprovalRequest entity - a single-holder (M=1) request is decided synchronously, in the
// same call that creates it, so there is nothing "pending" to track here, only the resolved outcome.
//
// Deliberately does not store the signed grant token itself (DESIGN.md Section 6.2, decision #13: never
// log/persist the raw grant) - only DecisionId (the grant's "jti" when approved), the outcome, and the
// request's own canonical fields for audit/traceability.
public class AuthorizationDecision
{
    public long Id { get; set; }
    public string DecisionId { get; set; } = null!;
    public string RequestedBy { get; set; } = null!;
    public string Operation { get; set; } = null!;
    public string DebitAccountId { get; set; } = null!;
    public string? CreditAccountId { get; set; }
    public string Amount { get; set; } = null!;
    public string Currency { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string? MandateId { get; set; }

    // T-020 addition (DESIGN.md Section 8.4/8.5): the reversal reason code (DUPL/TECH/FRAD/CUST/UNAUTH),
    // null for every other operation. Persisted here (not just in the signed grant claim) because the
    // grant itself is never logged or stored (decision #13) - without this column, the reason code would
    // be unrecoverable the moment the 30-second grant expires.
    public string? ReasonCode { get; set; }
    public bool Approved { get; set; }
    public string? DenialReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? GrantExpiresAt { get; set; }
}
