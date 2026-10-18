namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 3.8.2/3.8.4. Debit: a holder wants to move money and needs enough other holders'
// approval first. ThresholdChange: a holder wants to change the account's MandateApprovalPolicy and
// needs every current holder's approval (decision #22) - a stricter count than Debit's, but the same
// PendingApprovalRequest/ApprovalRecord shape, discriminated by this field.
public enum PendingApprovalRequestKind
{
    Debit,
    ThresholdChange
}
