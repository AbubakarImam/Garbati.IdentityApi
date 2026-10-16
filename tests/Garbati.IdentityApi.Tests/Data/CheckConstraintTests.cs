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
}
