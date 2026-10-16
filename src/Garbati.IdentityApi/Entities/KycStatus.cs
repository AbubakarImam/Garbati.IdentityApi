namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.1. Also reused by KycVerification.Status (Section 2.3): a verification attempt's
// own outcome and the customer's current standing are the same small set of values.
public enum KycStatus
{
    Pending,
    Verified,
    Rejected,
    Expired
}
