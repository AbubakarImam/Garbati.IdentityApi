namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 8.6, decision #34 - reconfirmed by garbati-platform T-021 (research/
// lien-auto-sweep-patterns.md, 18 Oct 2026): exactly these two values, nothing else. T-021 specifically
// checked whether a third "standing lien, auto-sweeps later" state belonged here and found no real-world
// precedent for it as a DEFAULT lien behavior - so this enum stays two-state by design, not by omission.
public enum ReversalOutcomeKind
{
    Completed,
    WriteOff,
}
