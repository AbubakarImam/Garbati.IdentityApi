namespace Garbati.IdentityApi.Contracts.Requests;

// T-044: PATCH /v1/customers/{customerId}/identity-numbers. Deliberately its own request type, separate
// from UpdateCustomerDetailsRequest (T-035) - identity numbers are set-once, not freely editable, so they
// get their own endpoint with different semantics rather than being added to that one (see the task
// file). Null means "not supplied, leave alone," exactly like UpdateCustomerDetailsRequest's convention;
// at least one of the two must be supplied (SubmitIdentityNumbersRequestValidator).
public record SubmitIdentityNumbersRequest(string? Bvn, string? Nin);
