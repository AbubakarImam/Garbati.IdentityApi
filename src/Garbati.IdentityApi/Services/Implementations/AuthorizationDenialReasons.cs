namespace Garbati.IdentityApi.Services;

// Stable reason codes for a denied authorization request (DESIGN.md Section 5.1: "a denial provides a
// stable reason code without exposing unnecessary sensitive details"). Returned to the caller and
// persisted on the AuthorizationDecision row - never anything more detailed that could leak internals.
public static class AuthorizationDenialReasons
{
    public const string CustomerNotFound = "CustomerNotFound";
    public const string CustomerNotActive = "CustomerNotActive";
    public const string CustomerNotVerified = "CustomerNotVerified";
    public const string OperationNotEligibleForKycTier = "OperationNotEligibleForKycTier";
    public const string NoActiveMandate = "NoActiveMandate";
    public const string OperationNotPermittedByMandate = "OperationNotPermittedByMandate";
    public const string ApprovalPolicyNotSingleHolder = "ApprovalPolicyNotSingleHolder";
}
