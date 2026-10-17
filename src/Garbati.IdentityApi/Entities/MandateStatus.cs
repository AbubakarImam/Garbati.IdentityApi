namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.2. A revoked mandate is retained for history - it is never deleted or silently
// reactivated (restoring access means creating a brand new Mandate row, decision #19).
public enum MandateStatus
{
    Active,
    Revoked
}
