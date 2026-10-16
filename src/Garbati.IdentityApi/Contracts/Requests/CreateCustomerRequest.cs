namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 5.1 / 2.1: creates a customer record. CustomerStatus = Active, KycStatus = Pending
// are set by the service, not accepted from the caller. No idempotency key - see the PR description for
// why (DESIGN.md Section 5.1 does not list one for this endpoint; raising it as a question rather than
// adding it unasked, per the task file).
public record CreateCustomerRequest(
    string FullName,
    string JurisdictionCode,
    string? CustomerReference,
    string? Email,
    string? PhoneNumber);
