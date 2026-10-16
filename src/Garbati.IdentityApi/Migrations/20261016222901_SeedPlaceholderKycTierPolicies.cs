using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Garbati.IdentityApi.Migrations
{
    // PLACEHOLDER DATA - not real Nigerian KYC tier limits. DESIGN.md decision #11/#24 only requires the
    // tier policy *shape* to be configurable, not hardcoded; the real evidence/operation lists and any
    // transaction limits still need compliance confirmation (garbati-platform/research/
    // nigerian-regulatory-requirements.md flags the exact figures as unverified against a primary CBN
    // circular). These three rows exist only to prove the shape works end to end - replace them with
    // confirmed values before relying on this table for anything real.
    /// <inheritdoc />
    public partial class SeedPlaceholderKycTierPolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO kyc_tier_policies (jurisdiction_code, tier_code, effective_from, effective_to, required_evidence, eligible_operations)
                VALUES
                    ('NG', 'Tier1', '2026-01-01T00:00:00Z', NULL, '["SelfDeclaredIdentity"]', '["View"]'),
                    ('NG', 'Tier2', '2026-01-01T00:00:00Z', NULL, '["SelfDeclaredIdentity", "GovernmentId"]', '["View", "Credit", "Debit"]'),
                    ('NG', 'Tier3', '2026-01-01T00:00:00Z', NULL, '["SelfDeclaredIdentity", "GovernmentId", "SourceOfFunds"]', '["View", "Credit", "Debit"]')
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM kyc_tier_policies
                WHERE jurisdiction_code = 'NG' AND tier_code IN ('Tier1', 'Tier2', 'Tier3');
                """);
        }
    }
}
