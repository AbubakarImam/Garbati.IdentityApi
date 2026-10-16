namespace Garbati.IdentityApi.Contracts.Outbox;

// The JSON shape of an OutboxMessage whose MessageType is OutboxMessageTypes.AccountFreezeRequested.
// This is the message the Closed/Deceased cascade (DESIGN.md Section 2.1, decision #14; Section 2.5)
// will eventually enqueue for every ledger account where the affected customer was the only active
// mandate holder, once T-017 adds Mandates and there is something real to look up.
//
// Defined now so the cascade's target shape is explicit and testable, even though T-016 has no Mandates
// table to populate it from yet - see CustomerService's cascade method and the PR description.
public record AccountFreezeRequestedPayload(
    string LedgerAccountId,
    string CustomerId,
    string Reason,
    DateTimeOffset RequestedAt);
