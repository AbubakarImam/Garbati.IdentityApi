namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 5.1 / 2.1: creates a customer record. CustomerStatus = Active, KycStatus = Pending
// are set by the service, not accepted from the caller.
//
// IdempotencyKey (decision #29): a client-supplied key, scoped to the calling API client and bound to a
// hash of this request, mirroring LedgerApi's POST /api/accounts (decision #43). A retry with the same
// key and the same other fields returns the originally-created customer instead of creating a second
// one; the same key with different fields is rejected (409). This was deliberately left out of the
// first build (see the T-016 PR) as a question rather than an assumption - the owner has since confirmed
// it belongs here, matching what OrchestratorApi's own design already assumed.
public record CreateCustomerRequest(
    string FullName,
    string JurisdictionCode,
    string? CustomerReference,
    string? Email,
    string? PhoneNumber,
    string IdempotencyKey) : IIdempotentRequest;
