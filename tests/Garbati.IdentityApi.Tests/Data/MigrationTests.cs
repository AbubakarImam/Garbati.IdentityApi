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
        Assert.Contains(applied, m => m.EndsWith("AddJointApprovalWorkflow"));
        Assert.Contains(applied, m => m.EndsWith("AddReversalAuthorization"));
        Assert.Contains(applied, m => m.EndsWith("AddBvnNinToCustomerIdentityData"));
        Assert.Contains(applied, m => m.EndsWith("AddBvnNinCardinalityToKycTierPolicies"));
        Assert.Contains(applied, m => m.EndsWith("AddAuthorizationDenialIdempotency"));
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
            "id", "customer_id", "full_name", "email", "phone_number", "bvn", "nin", "created_at", "updated_at");

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
            "amount", "currency", "idempotency_key", "mandate_id", "reason_code", "approved", "denial_reason",
            "created_at", "grant_expires_at");

        await AssertColumnsAsync(ctx, "pending_approval_requests",
            "id", "request_id", "kind", "ledger_account_id", "requested_by", "operation",
            "debit_account_id", "credit_account_id", "amount", "currency", "idempotency_key",
            "requested_required_approvals", "required_approvals", "status", "created_at",
            "expires_at", "decision_id");

        await AssertColumnsAsync(ctx, "approval_records",
            "id", "pending_approval_request_id", "customer_id", "approved_at", "voided_at", "voided_reason");

        await AssertColumnsAsync(ctx, "reversal_outcomes",
            "id", "authorization_decision_id", "outcome", "recorded_at", "recorded_by", "notes");
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

    // T-044 (garbati-platform D-26): AddBvnNinCardinalityToKycTierPolicies updates the same three
    // placeholder rows above to add the real BVN/NIN cardinality from research/
    // nigerian-regulatory-requirements.md Q1 - Tier1/Tier2 need at least one of Bvn/Nin, Tier3 needs
    // both - without dropping any of the pre-existing SelfDeclaredIdentity/GovernmentId/SourceOfFunds
    // tokens the original seed migration inserted.
    [Fact]
    public async Task CardinalityMigration_AddsBvnNinEvidenceTokens_WithoutRemovingExistingOnes()
    {
        await using var ctx = fixture.CreateContext();

        var tiers = await ctx.KycTierPolicies.AsNoTracking()
            .Where(p => p.JurisdictionCode == "NG")
            .ToDictionaryAsync(p => p.TierCode);

        Assert.Contains("SelfDeclaredIdentity", tiers["Tier1"].RequiredEvidence);
        Assert.Contains("AtLeastOneIdentityNumber", tiers["Tier1"].RequiredEvidence);

        Assert.Contains("GovernmentId", tiers["Tier2"].RequiredEvidence);
        Assert.Contains("AtLeastOneIdentityNumber", tiers["Tier2"].RequiredEvidence);

        Assert.Contains("SourceOfFunds", tiers["Tier3"].RequiredEvidence);
        Assert.Contains("BothIdentityNumbers", tiers["Tier3"].RequiredEvidence);
        // Tier3 needs BOTH, not "at least one" - the two tokens are not interchangeable.
        Assert.DoesNotContain("AtLeastOneIdentityNumber", tiers["Tier3"].RequiredEvidence);
    }

    // T-044: the AddBvnNinToCustomerIdentityData migration is additive only - no default value invented,
    // no data loss for any row that predates it. Modelled here as a row inserted via raw SQL without
    // mentioning the new columns at all (exactly what every row looked like before this migration
    // existed) - the real proof is that this still works post-migration and leaves both columns NULL,
    // not some other default.
    [Fact]
    public async Task AddBvnNinColumns_ExistingRowsGetNull_ForBothNewColumns_NoDefaultInvented()
    {
        await using var ctx = fixture.CreateContext();

        await ctx.Database.ExecuteSqlRawAsync("""
            INSERT INTO customers
                (customer_id, customer_status, kyc_status, jurisdiction_code, created_at, updated_at, idempotency_key, request_hash)
            VALUES
                ('CUS-PREMIGRATION', 'Active', 'Pending', 'NG', now(), now(), 'pre-migration-test-key', 'test-hash')
            """);
        await ctx.Database.ExecuteSqlRawAsync("""
            INSERT INTO customer_identity_data (customer_id, full_name, created_at, updated_at)
            SELECT id, 'Pre-Migration Customer', now(), now() FROM customers WHERE customer_id = 'CUS-PREMIGRATION'
            """);

        var identityData = await ctx.CustomerIdentityData.AsNoTracking()
            .SingleAsync(d => d.Customer.CustomerId == "CUS-PREMIGRATION");

        Assert.Null(identityData.Bvn);
        Assert.Null(identityData.Nin);
    }

    // T-049 (garbati-platform D-16): confirms the new index actually exists, is unique, and - critically -
    // is actually filtered (`WHERE approved = false`), via pg_indexes/pg_get_indexdef, which is the real
    // source of truth for what Postgres enforces (not just what the C# model claims to have asked for).
    [Fact]
    public async Task AuthorizationDenialIdempotencyIndex_ExistsAndIsFilteredOnApprovedFalse()
    {
        await using var ctx = fixture.CreateContext();
        var connection = ctx.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexdef FROM pg_indexes
            WHERE tablename = 'authorization_decisions'
              AND indexname = 'ix_authorization_decisions_requested_by_idempotency_key_denied'
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Expected index was not found via pg_indexes.");
        var indexDef = reader.GetString(0);

        Assert.Contains("UNIQUE", indexDef, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("requested_by", indexDef);
        Assert.Contains("idempotency_key", indexDef);
        Assert.Contains("WHERE (approved = false)", indexDef);
    }

    // Confirms what the index actually enforces, not just that it exists: two DENIED rows with the same
    // (requested_by, idempotency_key) collide at the database level, but two APPROVED rows with the exact
    // same pair do not - proving the `WHERE approved = false` filter is respected, not just declared.
    // Inserted directly via raw SQL (bypassing AuthorizationService entirely) so this is a real test of
    // the database constraint itself, independent of any application-level logic.
    [Fact]
    public async Task AuthorizationDenialIdempotencyIndex_BlocksTwoDeniedRows_ButAllowsTwoApprovedRows()
    {
        await using var ctx = fixture.CreateContext();

        var requestedBy = $"CUS-MIGTEST-{Guid.NewGuid():N}";
        var idempotencyKey = $"migtest-key-{Guid.NewGuid():N}";

        async Task InsertDecisionAsync(string decisionId, bool approved)
        {
            await ctx.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO authorization_decisions
                    (decision_id, requested_by, operation, debit_account_id, credit_account_id,
                     amount, currency, idempotency_key, approved, denial_reason, created_at)
                VALUES
                    ({decisionId}, {requestedBy}, 'Debit', 'ACC-1', 'ACC-2',
                     '5000', 'NGN', {idempotencyKey}, {approved}, {(string?)(approved ? null : "NoActiveMandate")}, now())
                """);
        }

        // Two APPROVED rows, same (requested_by, idempotency_key): must NOT violate the filtered index.
        await InsertDecisionAsync(Guid.NewGuid().ToString(), approved: true);
        await InsertDecisionAsync(Guid.NewGuid().ToString(), approved: true);

        // First DENIED row: succeeds. Second DENIED row with the same pair: must violate the index.
        await InsertDecisionAsync(Guid.NewGuid().ToString(), approved: false);

        // Raw SQL via ExecuteSqlRawAsync surfaces the driver's own exception directly (unlike
        // SaveChangesAsync, which EF wraps in DbUpdateException) - AuthorizationServiceTests already
        // covers the DbUpdateException-wrapped shape end to end through the real service.
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(
            () => InsertDecisionAsync(Guid.NewGuid().ToString(), approved: false));
        Assert.Equal("23505", ex.SqlState);
        Assert.Equal("ix_authorization_decisions_requested_by_idempotency_key_denied", ex.ConstraintName);
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
