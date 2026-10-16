namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.5 / 7.1: durable work for the Orchestrator to consume - e.g. "freeze this ledger
// account" - with retry and completion status tracked here, not assumed from the row merely existing.
// No consumer exists yet (the Orchestrator has no code), which is expected at this stage (see the task
// file and the PR description for T-016).
public class OutboxMessage
{
    public long Id { get; set; }
    public string MessageType { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public OutboxMessageStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
