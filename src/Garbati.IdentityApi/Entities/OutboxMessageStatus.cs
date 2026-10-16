namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.5: IdentityApi tracks acknowledgement state and retries until the Orchestrator
// confirms a queued action (e.g. a freeze) actually happened; a message is never "done" just because it
// was written.
public enum OutboxMessageStatus
{
    Pending,
    Sent,
    Acknowledged,
    Failed
}
