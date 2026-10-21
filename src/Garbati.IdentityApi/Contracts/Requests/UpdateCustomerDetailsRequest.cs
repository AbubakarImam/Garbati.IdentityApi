namespace Garbati.IdentityApi.Contracts.Requests;

// T-035 (garbati-platform D-19): a partial update for the three fields CustomerIdentityData defines
// (Entities/CustomerIdentityData.cs) - FullName, Email, PhoneNumber. Every field is optional; only the
// ones actually supplied (non-null) change. CustomerReference lives on Customer, not CustomerIdentityData,
// and is deliberately not part of this endpoint (see the task file) - extending scope beyond these three
// fields is not this task's call to make.
//
// No idempotency key: unlike CreateCustomerRequest, a field-level update has no "retry creates a
// duplicate" risk to guard against - setting the same value twice has no different effect than setting it
// once, the same reasoning that already applies to KycDecisionRequest and CustomerStatusChangeRequest.
public record UpdateCustomerDetailsRequest(string? FullName, string? Email, string? PhoneNumber);
