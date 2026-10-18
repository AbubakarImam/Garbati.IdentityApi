namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 8.6/7.1, decision #34, garbati-platform T-020. A thin audit record of a reversal
// authorization decision's ultimate recovery outcome - NOT a collections/lien/watch-listing workflow
// (explicitly out of scope, see the T-020 task file). IdentityApi never executes or tracks the recovery
// process itself (demand notices, watch-listing, legal referral); it only records that someone decided
// the outcome, what they decided, and when.
//
// AuthorizationDecisionId is a cross-TABLE reference within this same database (AuthorizationDecision's
// own DecisionId, not its internal numeric Id) - unlike LedgerAccountId elsewhere in this codebase, this
// is a real, enforced foreign key (DESIGN.md Section 7.4's "enforce within IdentityApi with a real
// database constraint" principle, applied here since both tables live in the same database).
public class ReversalOutcome
{
    public long Id { get; set; }
    public string AuthorizationDecisionId { get; set; } = null!;
    public ReversalOutcomeKind Outcome { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public string RecordedBy { get; set; } = null!;
    public string? Notes { get; set; }
}
