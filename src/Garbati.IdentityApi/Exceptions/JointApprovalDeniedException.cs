namespace Garbati.IdentityApi.Exceptions;

// The joint-approval-flow sibling of AuthorizationDeniedException, for denials that are not themselves a
// money-moving grant decision - a holder opening a ThresholdChange request who is not a current holder
// of the account, or an approver submitting .../approve without an Active mandate (or, for a Debit kind,
// without permission for the requested operation) on the account (DESIGN.md Section 3.8.1's approve
// validation list). Reuses AuthorizationDenialReasons' existing reason codes (NoActiveMandate,
// OperationNotPermittedByMandate) rather than inventing new ones for the same underlying facts. Unlike
// AuthorizationDeniedException, there is no decisionId here - no AuthorizationDecision row is created for
// these checks, since they are not a money-moving authorization decision. Mapped to 422, same
// business-rule-failure convention.
public class JointApprovalDeniedException(string reasonCode, string message) : Exception(message)
{
    public string ReasonCode { get; } = reasonCode;
}
