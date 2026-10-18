using Garbati.IdentityApi.Data;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Garbati.IdentityApi.Tests.Data;

// The C# enums keep bad values out of normal code paths; these tests prove the database refuses them
// too, through raw SQL that bypasses EF entirely (same intent as LedgerApi's CheckConstraintTests).
[Collection("Postgres")]
public class CheckConstraintTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Customer_WithUnknownCustomerStatus_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO customers (customer_id, customer_status, kyc_status, jurisdiction_code, created_at, updated_at)
            VALUES ('CUS-BADSTATUS', 'Vanished', 'Pending', 'NG', now(), now())
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_customers_customer_status", ex.ConstraintName);
    }

    [Fact]
    public async Task Customer_WithUnknownKycStatus_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO customers (customer_id, customer_status, kyc_status, jurisdiction_code, created_at, updated_at)
            VALUES ('CUS-BADKYC', 'Active', 'Unknown', 'NG', now(), now())
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_customers_kyc_status", ex.ConstraintName);
    }

    [Fact]
    public async Task Customer_WithADuplicateCustomerId_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO customers (customer_id, customer_status, kyc_status, jurisdiction_code, idempotency_key, created_at, updated_at)
            VALUES ('CUS-DUPLICATE', 'Active', 'Pending', 'NG', 'dup-key-1', now(), now())
            """);

        // A different idempotency_key: only the customer_id's own uniqueness is under test here.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO customers (customer_id, customer_status, kyc_status, jurisdiction_code, idempotency_key, created_at, updated_at)
            VALUES ('CUS-DUPLICATE', 'Active', 'Pending', 'NG', 'dup-key-2', now(), now())
            """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("ix_customers_customer_id", ex.ConstraintName);
    }

    [Fact]
    public async Task ValidCustomerStatusesAndKycStatuses_AreAccepted()
    {
        await using var ctx = fixture.CreateContext();

        // Distinct idempotency_key per row: all four would otherwise share the column default ('') with
        // the same (null) initiated_by, colliding on ix_customers_initiated_by_idempotency_key.
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO customers (customer_id, customer_status, kyc_status, jurisdiction_code, idempotency_key, created_at, updated_at)
            VALUES ('CUS-OK1', 'Active', 'Pending', 'NG', 'ok-key-1', now(), now()),
                   ('CUS-OK2', 'Suspended', 'Verified', 'NG', 'ok-key-2', now(), now()),
                   ('CUS-OK3', 'Closed', 'Expired', 'NG', 'ok-key-3', now(), now()),
                   ('CUS-OK4', 'Deceased', 'Rejected', 'NG', 'ok-key-4', now(), now())
            """);

        Assert.Equal(4, await ctx.Customers.CountAsync());
    }

    private async Task<long> InsertCustomerAsync(IdentityDbContext ctx, string customerId)
    {
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO customers (customer_id, customer_status, kyc_status, jurisdiction_code, idempotency_key, created_at, updated_at)
            VALUES ({customerId}, 'Active', 'Pending', 'NG', {customerId}, now(), now())
            """);
        return await ctx.Customers.Where(c => c.CustomerId == customerId).Select(c => c.Id).SingleAsync();
    }

    [Fact]
    public async Task Mandate_WithUnknownStatus_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();
        var customerPk = await InsertCustomerAsync(ctx, "CUS-MANDATE-BADSTATUS");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by)
            VALUES ('MND-BADSTATUS', {customerPk}, 'ACC-1', 'Vanished', now(), 'test')
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_mandates_status", ex.ConstraintName);
    }

    [Fact]
    public async Task MandatePermission_WithUnknownOperation_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();
        var customerPk = await InsertCustomerAsync(ctx, "CUS-MANDATE-BADOP");
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by)
            VALUES ('MND-BADOP', {customerPk}, 'ACC-1', 'Active', now(), 'test')
            """);
        var mandatePk = await ctx.Mandates.Where(m => m.MandateId == "MND-BADOP").Select(m => m.Id).SingleAsync();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandate_permissions (mandate_id, operation) VALUES ({mandatePk}, 'Transfer')
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_mandate_permissions_operation", ex.ConstraintName);
    }

    // DESIGN.md Section 2.2/7.1/7.4, decision #19: the real constraint, not just application validation.
    [Fact]
    public async Task Mandate_SecondActiveMandateForTheSameCustomerAndAccount_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();
        var customerPk = await InsertCustomerAsync(ctx, "CUS-MANDATE-DUP");
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by)
            VALUES ('MND-DUP-1', {customerPk}, 'ACC-DUP', 'Active', now(), 'test')
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by)
            VALUES ('MND-DUP-2', {customerPk}, 'ACC-DUP', 'Active', now(), 'test')
            """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("ix_mandates_customer_account_active", ex.ConstraintName);
    }

    // The partial index only covers Status = 'Active' - a second, REVOKED mandate for the same pair (a
    // customer regaining access through a brand new mandate after an earlier one was revoked) must be
    // allowed, and so must a second customer's own active mandate on the same account (joint accounts).
    [Fact]
    public async Task Mandate_SecondMandateForTheSamePair_IsAccepted_WhenTheFirstIsRevoked()
    {
        await using var ctx = fixture.CreateContext();
        var customerPk = await InsertCustomerAsync(ctx, "CUS-MANDATE-REVOKED-THEN-NEW");
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by, revoked_at, revoked_by, revocation_reason)
            VALUES ('MND-OLD', {customerPk}, 'ACC-REISSUE', 'Revoked', now(), 'test', now(), 'test', 'closed')
            """);

        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by)
            VALUES ('MND-NEW', {customerPk}, 'ACC-REISSUE', 'Active', now(), 'test')
            """);

        Assert.Equal(2, await ctx.Mandates.CountAsync(m => m.LedgerAccountId == "ACC-REISSUE"));
    }

    // T-018, DESIGN.md Section 3.8.2/7.1.
    [Fact]
    public async Task PendingApprovalRequest_WithUnknownKind_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pending_approval_requests
                (request_id, kind, ledger_account_id, requested_by, required_approvals, status, created_at, expires_at)
            VALUES ('PAR-BADKIND', 'Transfer', 'ACC-1', 'CUS-1', 2, 'Pending', now(), now())
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_pending_approval_requests_kind", ex.ConstraintName);
    }

    [Fact]
    public async Task PendingApprovalRequest_WithUnknownStatus_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pending_approval_requests
                (request_id, kind, ledger_account_id, requested_by, required_approvals, status, created_at, expires_at)
            VALUES ('PAR-BADSTATUS', 'Debit', 'ACC-1', 'CUS-1', 2, 'Vanished', now(), now())
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_pending_approval_requests_status", ex.ConstraintName);
    }

    // DESIGN.md Section 3.8.2: "a holder cannot approve the same request twice" - the real constraint,
    // not just application validation (decision #19's pattern, applied here).
    [Fact]
    public async Task ApprovalRecord_SecondApprovalForTheSameRequestAndCustomer_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO pending_approval_requests
                (request_id, kind, ledger_account_id, requested_by, required_approvals, status, created_at, expires_at)
            VALUES ('PAR-DUP', 'Debit', 'ACC-1', 'CUS-1', 2, 'Pending', now(), now())
            """);
        var requestPk = await ctx.PendingApprovalRequests.Where(r => r.RequestId == "PAR-DUP").Select(r => r.Id).SingleAsync();

        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO approval_records (pending_approval_request_id, customer_id, approved_at)
            VALUES ({requestPk}, 'CUS-1', now())
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO approval_records (pending_approval_request_id, customer_id, approved_at)
            VALUES ({requestPk}, 'CUS-1', now())
            """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("ix_approval_records_request_customer", ex.ConstraintName);
    }

    // T-020, DESIGN.md Section 8.6/7.1, decision #34.
    private async Task InsertApprovedReversalDecisionAsync(IdentityDbContext ctx, string decisionId)
    {
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO authorization_decisions
                (decision_id, requested_by, operation, debit_account_id, amount, currency, idempotency_key,
                 reason_code, approved, created_at)
            VALUES ({decisionId}, 'OPS-ACTOR', 'Reversal', 'TXN-1', '100', 'NGN', {decisionId}, 'FRAD', true, now())
            """);
    }

    [Fact]
    public async Task ReversalOutcome_WithUnknownOutcome_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();
        await InsertApprovedReversalDecisionAsync(ctx, "DEC-REV-BADOUTCOME");

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reversal_outcomes (authorization_decision_id, outcome, recorded_at, recorded_by)
            VALUES ('DEC-REV-BADOUTCOME', 'Vanished', now(), 'test')
            """));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal("ck_reversal_outcomes_outcome", ex.ConstraintName);
    }

    // DESIGN.md Section 8.6: "at most one outcome per decision" - the real constraint, not just
    // application validation (decision #19/#34's established pattern, applied here).
    [Fact]
    public async Task ReversalOutcome_SecondOutcomeForTheSameDecision_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();
        await InsertApprovedReversalDecisionAsync(ctx, "DEC-REV-DUP");
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reversal_outcomes (authorization_decision_id, outcome, recorded_at, recorded_by)
            VALUES ('DEC-REV-DUP', 'Completed', now(), 'ops-user-1')
            """);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reversal_outcomes (authorization_decision_id, outcome, recorded_at, recorded_by)
            VALUES ('DEC-REV-DUP', 'WriteOff', now(), 'ops-user-2')
            """));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        Assert.Equal("ix_reversal_outcomes_authorization_decision_id", ex.ConstraintName);
    }

    // DESIGN.md Section 7.4: a real foreign key, not just an opaque string - unlike LedgerAccountId
    // elsewhere in this schema, both tables live in the same database.
    [Fact]
    public async Task ReversalOutcome_ForANonexistentAuthorizationDecisionId_IsRejectedByTheDatabase()
    {
        await using var ctx = fixture.CreateContext();

        var ex = await Assert.ThrowsAsync<PostgresException>(() => ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reversal_outcomes (authorization_decision_id, outcome, recorded_at, recorded_by)
            VALUES ('DEC-DOES-NOT-EXIST', 'Completed', now(), 'ops-user-1')
            """));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
    }

    [Fact]
    public async Task Mandate_TwoDifferentCustomers_CanEachHoldAnActiveMandateOnTheSameAccount()
    {
        await using var ctx = fixture.CreateContext();
        var customerAPk = await InsertCustomerAsync(ctx, "CUS-JOINT-A");
        var customerBPk = await InsertCustomerAsync(ctx, "CUS-JOINT-B");

        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by)
            VALUES ('MND-JOINT-A', {customerAPk}, 'ACC-JOINT', 'Active', now(), 'test')
            """);
        await ctx.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO mandates (mandate_id, customer_id, ledger_account_id, status, created_at, created_by)
            VALUES ('MND-JOINT-B', {customerBPk}, 'ACC-JOINT', 'Active', now(), 'test')
            """);

        Assert.Equal(2, await ctx.Mandates.CountAsync(m => m.LedgerAccountId == "ACC-JOINT" && m.Status == MandateStatus.Active));
    }
}
