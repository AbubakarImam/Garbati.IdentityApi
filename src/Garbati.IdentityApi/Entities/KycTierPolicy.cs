namespace Garbati.IdentityApi.Entities;

// DESIGN.md Section 2.3 / decision #11 (#24 confirmed): tier rules are configurable policy data, not
// hardcoded in application code. RequiredEvidence/EligibleOperations are kept as flexible string lists
// (stored as jsonb - see IdentityDbContext) rather than fixed columns, since the real evidence/operation
// vocabulary for Nigeria is still pending compliance confirmation (garbati-platform/research/
// nigerian-regulatory-requirements.md). The seeded rows (see the SeedPlaceholderKycTierPolicies
// migration) are explicitly placeholders, not real Nigerian tier limits.
public class KycTierPolicy
{
    public long Id { get; set; }
    public string JurisdictionCode { get; set; } = null!;
    public string TierCode { get; set; } = null!;
    public DateTimeOffset EffectiveFrom { get; set; }
    public DateTimeOffset? EffectiveTo { get; set; }
    public List<string> RequiredEvidence { get; set; } = [];
    public List<string> EligibleOperations { get; set; } = [];
}
