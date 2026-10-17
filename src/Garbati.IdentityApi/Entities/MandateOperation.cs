namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.2 (MandatePermissions.Operation). The same vocabulary is reused as the
// authorization request's Operation field (Section 3.3/3.8.2) - a request is only approved when the
// requester's mandate carries a permission row for the exact operation asked for.
public enum MandateOperation
{
    View,
    Debit,
    Credit
}
