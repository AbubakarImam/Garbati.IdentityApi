namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 6.3 / decision #13: a durable, append-only audit trail, separate from diagnostic
// application logs. Shape mirrors LedgerApi's AuditLog, generalized: Details carries whatever business
// context an action needs (e.g. old/new status and reason) instead of ledger-specific columns.
public class AuditEvent
{
    public long Id { get; set; }
    public string? ActorId { get; set; }
    public string Action { get; set; } = null!;
    public string EntityType { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public string? Details { get; set; }
    public string? Status { get; set; }
    public string? CorrelationId { get; set; }
    public string? Endpoint { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
