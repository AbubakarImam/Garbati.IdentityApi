namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 8.4, decision #32: the exact five reason codes a Reversal authorization request may
// carry - reusing converged ISO 20022/card-network vocabulary, plus one GARBATI-specific addition
// (UNAUTH, since no single ISO 20022 code maps to a consumer-facing "I never authorized this" claim).
// DISPUTE (a card-network-style consumer-dispute category) is deliberately NOT a member here - it is
// explicitly deferred (8.4) until a real financing/merchant product exists, not an oversight.
public enum ReversalReasonCode
{
    DUPL, // Duplicate posting
    TECH, // Technical/processing error
    FRAD, // Fraudulent origin
    CUST, // Customer requested, no error claimed - the ops channel's own process confirms cooperation
    UNAUTH, // Customer disputes the transaction as never authorized by them
}
