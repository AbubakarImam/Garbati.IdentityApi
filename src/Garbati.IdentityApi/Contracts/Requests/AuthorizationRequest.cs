namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 3.8.2's canonical fields (even though the joint-account PendingApprovalRequest
// entity itself isn't built here - see the task file). RequestedBy is the requesting customer's public
// CustomerId. No idempotency *enforcement* happens on this field here (decision left open in the task
// file, see the PR description) - IdempotencyKey is still required input because it is one of the
// canonical fields bound into the signed grant (Section 3.3).
//
// T-020 additions (DESIGN.md Section 8.3): Operation == "Reversal" is a different request shape layered
// onto this same contract rather than a new one (decision #33: "same claim shape"). Judgment call (task
// file: "a reference to the original transaction/chain being reversed, replacing debit/credit accounts
// for this operation type ... leave an equally clear comment, don't silently skip the question"): rather
// than add a brand-new field, DebitAccountId itself carries that single reference for a Reversal request
// - there is no dedicated "original transaction reference" field because decision #33 says the grant's
// claim shape does not grow beyond the reason code, and the request contract mirrors the grant 1:1
// elsewhere in this file already. CreditAccountId is therefore nullable - a reversal has no destination
// account to name (T-017's own #TBD-8 flagged this exact case: "revisit if/when an operation without a
// destination account is actually designed"). ReasonCode is required, and validated against the fixed
// list in ReversalReasonCode, only when Operation == "Reversal" (AuthorizationRequestValidator); it is
// ignored for every other operation.
public record AuthorizationRequest(
    string RequestedBy,
    string Operation,
    string DebitAccountId,
    string? CreditAccountId,
    decimal Amount,
    string Currency,
    string IdempotencyKey,
    string? ReasonCode = null);
