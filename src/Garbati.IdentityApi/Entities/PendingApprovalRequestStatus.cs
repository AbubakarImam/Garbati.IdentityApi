namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 3.8.2/3.8.3/7.3. Terminal states (Expired, Cancelled, Approved) are never deleted and
// never revisited - a late approval after Expired is rejected, not honored (decision #23). Cancelled
// exists in the enum per the task's own entity list but nothing in this task creates one - there is no
// "withdraw my own request" endpoint in scope; it is here so the column's check constraint matches the
// full vocabulary the design names, not because any code path writes it yet.
public enum PendingApprovalRequestStatus
{
    Pending,
    Approved,
    Expired,
    Cancelled
}
