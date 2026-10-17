using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Garbati.IdentityApi.Tests.Data;

// Confirms the migrated database schema actually matches the entities in Entities/ - not just that EF's
// in-memory model does (mirrors LedgerApi's migration-test style, e.g. ChainMigrationTests).
[Collection("Postgres")]
public class MigrationTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AppliedMigrations_IncludeBothMigrations()
    {
        await using var ctx = fixture.CreateContext();

        var applied = await ctx.Database.GetAppliedMigrationsAsync();

        Assert.Contains(applied, m => m.EndsWith("InitialCreate"));
        Assert.Contains(applied, m => m.EndsWith("SeedPlaceholderKycTierPolicies"));
        Assert.Contains(applied, m => m.EndsWith("AddIdempotencyToCustomers"));
        Assert.Contains(applied, m => m.EndsWith("AddMandatesAndAuthorization"));
    }

    [Fact]
    public async Task EveryEntityTable_ExistsWithItsExpectedColumns()
    {
        await using var ctx = fixture.CreateContext();

        await AssertColumnsAsync(ctx, "customers",
            "id", "customer_id", "customer_reference", "customer_status", "kyc_status",
            "kyc_tier", "jurisdiction_code", "created_at", "updated_at",
            "idempotency_key", "initiated_by", "request_hash");

        await AssertColumnsAsync(ctx, "customer_identity_data",
            "id", "customer_id", "full_name", "email", "phone_number", "created_at", "updated_at");

        await AssertColumnsAsync(ctx, "kyc_verifications",
            "id", "customer_id", "verification_type", "provider", "provider_reference",
            "status", "result_code", "verified_at", "expires_at", "created_at");

        await AssertColumnsAsync(ctx, "kyc_tier_policies",
            "id", "jurisdiction_code", "tier_code", "effective_from", "effective_to",
            "required_evidence", "eligible_operations");

        await AssertColumnsAsync(ctx, "audit_events",
            "id", "actor_id", "action", "entity_type", "entity_id", "details", "status",
            "correlation_id", "endpoint", "ip_address", "user_agent", "created_at");

        await AssertColumnsAsync(ctx, "outbox_messages",
            "id", "message_type", "payload", "status", "attempt_count", "last_error", "created_at", "processed_at");

        await AssertColumnsAsync(ctx, "mandates",
            "id", "mandate_id", "customer_id", "ledger_account_id", "status", "created_at", "created_by",
            "revoked_at", "revoked_by", "revocation_reason");

        await AssertColumnsAsync(ctx, "mandate_permissions", "id", "mandate_id", "operation");

        await AssertColumnsAsync(ctx, "mandate_approval_policies",
            "id", "ledger_account_id", "required_approvals", "created_at", "last_changed_at", "last_changed_by");

        await AssertColumnsAsync(ctx, "authorization_decisions",
            "id", "decision_id", "requested_by", "operation", "debit_account_id", "credit_account_id",
            "amount", "currency", "idempotency_key", "mandate_id", "approved", "denial_reason",
            "created_at", "grant_expires_at");
    }

    [Fact]
    public async Task SeedMigration_InsertsThreePlaceholderNigerianTierPolicies()
    {
        await using var ctx = fixture.CreateContext();

        var tiers = await ctx.KycTierPolicies.AsNoTracking()
            .Where(p => p.JurisdictionCode == "NG")
            .OrderBy(p => p.TierCode)
            .ToListAsync();

        Assert.Equal(["Tier1", "Tier2", "Tier3"], tiers.Select(t => t.TierCode));
        Assert.All(tiers, t => Assert.NotEmpty(t.RequiredEvidence));
    }

    private static async Task AssertColumnsAsync(Microsoft.EntityFrameworkCore.DbContext ctx, string table, params string[] expectedColumns)
    {
        var actual = new List<string>();
        // Reuses EF's own connection without disposing it (EF owns its lifecycle) - this helper runs
        // several times against the same context within one test.
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = @table";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = table;
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(0));
        }

        foreach (var expected in expectedColumns)
        {
            Assert.Contains(expected, actual);
        }
    }
}
