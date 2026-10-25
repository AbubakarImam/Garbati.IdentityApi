namespace Garbati.IdentityApi.Entities;

// T-044 (garbati-platform D-26/Q1): the two evidence tokens CustomerService.RecordKycDecisionAsync
// actually understands and enforces, out of KycTierPolicy.RequiredEvidence's open, configurable jsonb
// vocabulary (decision #11). Deliberately narrow - SelfDeclaredIdentity/GovernmentId/SourceOfFunds are
// also valid tokens in that same list (see SeedPlaceholderKycTierPolicies) but have no backing data
// anywhere in the system yet, so they stay exactly as unenforced as before this task (see the task file's
// "Enforcement scope" note). Adding a token here is a deliberate, reviewed decision, not something a new
// string in the jsonb column can trigger on its own.
public static class KycEvidenceTokens
{
    // Satisfied when CustomerIdentityData.Bvn or .Nin is non-null (Tier1/Tier2, Q1's table).
    public const string AtLeastOneIdentityNumber = "AtLeastOneIdentityNumber";

    // Satisfied only when both CustomerIdentityData.Bvn and .Nin are non-null (Tier3, Q1's table).
    public const string BothIdentityNumbers = "BothIdentityNumbers";
}
