namespace Garbati.IdentityApi.Contracts.Requests;

// DESIGN.md Section 5.1 / 2.3: records a KYC verification attempt and, when it succeeds, the resulting
// tier. KycStatus is a string here (validated against the enum by KycDecisionRequestValidator) so an
// unrecognized value is a clean 400, not an unhandled exception.
public record KycDecisionRequest(
    string KycStatus,
    string? KycTier,
    string VerificationType,
    string Provider,
    string? ProviderReference,
    string? ResultCode,
    DateTimeOffset? ExpiresAt);
