namespace Garbati.IdentityApi.Entities;

// Known OutboxMessage.MessageType values. AccountFreezeRequested is the shape the Closed/Deceased
// cascade (DESIGN.md Section 2.1, decision #14) will enqueue once T-017 adds Mandates and there is
// something real to look up - see CustomerService's cascade method and the PR description for why no
// row of this type is written yet.
public static class OutboxMessageTypes
{
    public const string AccountFreezeRequested = "AccountFreezeRequested";
}
