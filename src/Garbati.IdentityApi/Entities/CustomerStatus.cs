namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.1. Closed and Deceased are terminal: no transition out of either is allowed.
public enum CustomerStatus
{
    Active,
    Suspended,
    Deceased,
    Closed
}
