namespace Garbati.IdentityApi.Auditing;

// The state changes that are audited (DESIGN.md Section 6.3). Outcome is in AuditEvent.Status, not here.
public static class AuditActions
{
    public const string CustomerCreated = "CustomerCreated";
    public const string KycDecisionRecorded = "KycDecisionRecorded";
    public const string CustomerSuspended = "CustomerSuspended";
    public const string CustomerReactivated = "CustomerReactivated";
    public const string CustomerClosed = "CustomerClosed";
    public const string CustomerMarkedDeceased = "CustomerMarkedDeceased";

    // T-017 additions (DESIGN.md Section 6.3).
    public const string MandateCreated = "MandateCreated";
    public const string MandateRevoked = "MandateRevoked";
    public const string AccountFreezeRequested = "AccountFreezeRequested";
    public const string AuthorizationApproved = "AuthorizationApproved";
    public const string AuthorizationDenied = "AuthorizationDenied";
}
