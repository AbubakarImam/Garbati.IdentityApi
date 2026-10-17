namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 5.1: POST /v1/customers/{customerId}/mandates. LedgerAccountId is a plain opaque
// string (Section 2.2) - not a foreign key to anything, LedgerApi's database is separate. Operations is
// the set of MandateOperation values (View/Debit/Credit) this mandate grants, validated by
// AttachMandateRequestValidator.
public record AttachMandateRequest(string LedgerAccountId, List<string> Operations);
