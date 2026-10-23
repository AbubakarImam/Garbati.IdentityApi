using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    // T-044 (garbati-platform D-26, Q1): updates the three placeholder rows SeedPlaceholderKycTierPolicies
    // inserted, per the task file's explicit instruction to do this via a NEW migration rather than
    // editing that old one. Adds to each row's existing RequiredEvidence list (never replaces it) the
    // real Nigerian BVN/NIN cardinality from Q1's table: Tier1/Tier2 need at least one of Bvn/Nin, Tier3
    // needs both. These rows are still placeholder data overall (the original migration's own comment
    // still applies to SelfDeclaredIdentity/GovernmentId/SourceOfFunds) - only the BVN/NIN cardinality
    // piece being added here is asserted as an independently-confirmed real fact (research/
    // nigerian-regulatory-requirements.md Q1).
    /// <inheritdoc />
    public partial class AddBvnNinCardinalityToKycTierPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE kyc_tier_policies
                SET required_evidence = required_evidence || '["AtLeastOneIdentityNumber"]'::jsonb
                WHERE jurisdiction_code = 'NG' AND tier_code IN ('Tier1', 'Tier2')
                  AND NOT (required_evidence @> '["AtLeastOneIdentityNumber"]'::jsonb);

                UPDATE kyc_tier_policies
                SET required_evidence = required_evidence || '["BothIdentityNumbers"]'::jsonb
                WHERE jurisdiction_code = 'NG' AND tier_code = 'Tier3'
                  AND NOT (required_evidence @> '["BothIdentityNumbers"]'::jsonb);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE kyc_tier_policies
                SET required_evidence = (
                    SELECT COALESCE(jsonb_agg(elem), '[]'::jsonb)
                    FROM jsonb_array_elements(required_evidence) elem
                    WHERE elem <> '"AtLeastOneIdentityNumber"'::jsonb
                )
                WHERE jurisdiction_code = 'NG' AND tier_code IN ('Tier1', 'Tier2');

                UPDATE kyc_tier_policies
                SET required_evidence = (
                    SELECT COALESCE(jsonb_agg(elem), '[]'::jsonb)
                    FROM jsonb_array_elements(required_evidence) elem
                    WHERE elem <> '"BothIdentityNumbers"'::jsonb
                )
                WHERE jurisdiction_code = 'NG' AND tier_code = 'Tier3';
                """);
        }
    }
}
