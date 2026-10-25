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

    // T-018 additions (DESIGN.md Section 6.3: "every PendingApprovalRequest lifecycle event: created,
    // each approval given, voided (and why), expired, approved/grant issued" plus "every threshold-change
    // request and its outcome"). One set of actions covers both Kind values; Details distinguishes.
    public const string PendingApprovalRequestCreated = "PendingApprovalRequestCreated";
    public const string ApprovalGiven = "ApprovalGiven";
    public const string ApprovalVoided = "ApprovalVoided";
    public const string PendingApprovalRequestExpired = "PendingApprovalRequestExpired";
    public const string PendingApprovalRequestResolved = "PendingApprovalRequestResolved";

    // T-020 additions (DESIGN.md Section 6.3/8, garbati-platform T-020): a reversal authorization decision
    // uses the same AuthorizationApproved action above when granted (ReversalAuthorizationService reuses
    // AuthorizationDecision exactly like every other operation) - there is no separate "requested" event
    // because, per decision #30/#31, there is no separate request/decide phase to split apart (unlike the
    // T-018 PendingApprovalRequest lifecycle, this is a single synchronous call, same as a single-holder
    // Debit before T-018 existed). ReversalOutcomeRecorded is the one genuinely new lifecycle event.
    public const string ReversalOutcomeRecorded = "ReversalOutcomeRecorded";

    // T-035 addition (DESIGN.md Section 6.3: "Customer creation and privileged identity-data changes"):
    // PATCH /v1/customers/{customerId}/details, the maker-checker edit OpsApi's T-034 workflow applies
    // once a CustomerServiceOfficer's staged change is Supervisor-approved.
    public const string CustomerDetailsUpdated = "CustomerDetailsUpdated";

    // T-044 addition (DESIGN.md Section 6.3, garbati-platform D-26): PATCH
    // /v1/customers/{customerId}/identity-numbers. Recorded only when a value actually changes (the
    // set-once no-op case writes no audit row - see CustomerService.SubmitIdentityNumbersAsync's own
    // comment for why). Details names only which field(s) changed ("Bvn set"/"Nin set") - never the
    // submitted value itself (ARCHITECTURE.md Section 5: "Logs never contain identity numbers").
    public const string IdentityNumberSubmitted = "IdentityNumberSubmitted";
}
