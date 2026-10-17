using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Garbati.IdentityApi.Tests.Services;

[Collection("Postgres")]
public class MandateServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> CreateCustomerAsync(string fullName = "Ada Lovelace")
    {
        await using var ctx = fixture.CreateContext();
        var request = new CreateCustomerRequest(fullName, "NG", null, null, null, Guid.NewGuid().ToString());
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).CreateCustomerAsync(request);
        return response.CustomerId;
    }

    [Fact]
    public async Task AttachMandateAsync_CreatesAnActiveMandate_WithItsPermissions()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-1", ["View", "Debit"]));

        Assert.StartsWith("MND-", response.MandateId);
        Assert.Equal(customerId, response.CustomerId);
        Assert.Equal("ACC-1", response.LedgerAccountId);
        Assert.Equal("Active", response.Status);
        Assert.Equal(["Debit", "View"], response.Operations);
        Assert.Null(response.RevokedAt);
    }

    [Fact]
    public async Task AttachMandateAsync_CreatesTheApprovalPolicy_WithRequiredApprovalsOfOne()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-2", ["View"]));

        await using var verifyCtx = fixture.CreateContext();
        var policy = await verifyCtx.MandateApprovalPolicies.SingleAsync(p => p.LedgerAccountId == "ACC-2");
        Assert.Equal(1, policy.RequiredApprovals);
    }

    [Fact]
    public async Task AttachMandateAsync_DoesNotCreateASecondApprovalPolicy_ForASecondHolderOnTheSameAccount()
    {
        var customerA = await CreateCustomerAsync("Customer A");
        var customerB = await CreateCustomerAsync("Customer B");

        await using var ctx = fixture.CreateContext();
        var service = new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default);
        await service.AttachMandateAsync(customerA, new AttachMandateRequest("ACC-JOINT", ["View"]));
        await service.AttachMandateAsync(customerB, new AttachMandateRequest("ACC-JOINT", ["View"]));

        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(1, await verifyCtx.MandateApprovalPolicies.CountAsync(p => p.LedgerAccountId == "ACC-JOINT"));
        Assert.Equal(2, await verifyCtx.Mandates.CountAsync(m => m.LedgerAccountId == "ACC-JOINT"));
    }

    [Fact]
    public async Task AttachMandateAsync_RecordsAnAuditRow()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-3", ["Debit"]));

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.MandateCreated);
        Assert.Equal(response.MandateId, audit.EntityId);
    }

    [Fact]
    public async Task AttachMandateAsync_Throws_ForAnUnknownCustomer()
    {
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .AttachMandateAsync("CUS-DOESNOTEXIST", new AttachMandateRequest("ACC-1", ["View"])));
    }

    // DESIGN.md Section 2.2/7.1/7.4, decision #19: the same customer cannot get two active mandates on
    // the same account; two DIFFERENT customers can each have their own.
    [Fact]
    public async Task AttachMandateAsync_Throws_WhenTheSameCustomerAlreadyHasAnActiveMandateOnTheAccount()
    {
        var customerId = await CreateCustomerAsync();
        await using (var firstCtx = fixture.CreateContext())
        {
            await new MandateService(firstCtx, TestAudit.For(firstCtx), TestCaller.Default)
                .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-DUP", ["View"]));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<DuplicateActiveMandateException>(() =>
            new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-DUP", ["Debit"])));

        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(1, await verifyCtx.Mandates.CountAsync(m => m.LedgerAccountId == "ACC-DUP"));
    }

    [Fact]
    public async Task AttachMandateAsync_Succeeds_WhenADifferentCustomerAlreadyHoldsTheAccount()
    {
        var customerA = await CreateCustomerAsync("Customer A");
        var customerB = await CreateCustomerAsync("Customer B");

        await using var ctx = fixture.CreateContext();
        var service = new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default);
        await service.AttachMandateAsync(customerA, new AttachMandateRequest("ACC-SHARED", ["View"]));
        var second = await service.AttachMandateAsync(customerB, new AttachMandateRequest("ACC-SHARED", ["View"]));

        Assert.Equal("Active", second.Status);
        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(2, await verifyCtx.Mandates.CountAsync(m => m.LedgerAccountId == "ACC-SHARED" && m.Status == MandateStatus.Active));
    }

    [Fact]
    public async Task ListMandatesForCustomerAsync_ReturnsOnlyThatCustomersMandates()
    {
        var customerA = await CreateCustomerAsync("Customer A");
        var customerB = await CreateCustomerAsync("Customer B");

        await using (var ctx = fixture.CreateContext())
        {
            var service = new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default);
            await service.AttachMandateAsync(customerA, new AttachMandateRequest("ACC-A1", ["View"]));
            await service.AttachMandateAsync(customerA, new AttachMandateRequest("ACC-A2", ["View"]));
            await service.AttachMandateAsync(customerB, new AttachMandateRequest("ACC-B1", ["View"]));
        }

        await using var verifyCtx = fixture.CreateContext();
        var mandates = await new MandateService(verifyCtx, TestAudit.For(verifyCtx), TestCaller.Default)
            .ListMandatesForCustomerAsync(customerA);

        Assert.Equal(2, mandates.Count);
        Assert.All(mandates, m => Assert.Equal(customerA, m.CustomerId));
    }

    [Fact]
    public async Task ListMandatesForCustomerAsync_Throws_ForAnUnknownCustomer()
    {
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default).ListMandatesForCustomerAsync("CUS-DOESNOTEXIST"));
    }

    [Fact]
    public async Task ListActiveHoldersForAccountAsync_ReturnsOnlyActiveMandatesOnThatAccount()
    {
        var customerA = await CreateCustomerAsync("Customer A");
        var customerB = await CreateCustomerAsync("Customer B");
        string mandateToRevoke;

        await using (var ctx = fixture.CreateContext())
        {
            var service = new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default);
            var mandate = await service.AttachMandateAsync(customerA, new AttachMandateRequest("ACC-HOLDERS", ["View"]));
            await service.AttachMandateAsync(customerB, new AttachMandateRequest("ACC-HOLDERS", ["View"]));
            mandateToRevoke = mandate.MandateId;
        }

        await using (var ctx = fixture.CreateContext())
        {
            await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .RevokeMandateAsync(mandateToRevoke, new RevokeMandateRequest("no longer needed"));
        }

        await using var verifyCtx = fixture.CreateContext();
        var holders = await new MandateService(verifyCtx, TestAudit.For(verifyCtx), TestCaller.Default)
            .ListActiveHoldersForAccountAsync("ACC-HOLDERS");

        Assert.Single(holders);
        Assert.Equal(customerB, holders[0].CustomerId);
    }

    [Fact]
    public async Task ListActiveHoldersForAccountAsync_ReturnsEmpty_ForAnAccountWithNoMandates()
    {
        await using var ctx = fixture.CreateContext();
        var holders = await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .ListActiveHoldersForAccountAsync("ACC-DOESNOTEXIST");

        Assert.Empty(holders);
    }

    [Fact]
    public async Task GetMandateAsync_ReturnsNull_WhenMandateDoesNotExist()
    {
        await using var ctx = fixture.CreateContext();
        var mandate = await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default).GetMandateAsync("MND-DOESNOTEXIST");

        Assert.Null(mandate);
    }

    [Fact]
    public async Task RevokeMandateAsync_SetsStatusToRevoked_WithActorAndReason()
    {
        var customerId = await CreateCustomerAsync();
        string mandateId;
        await using (var ctx = fixture.CreateContext())
        {
            var mandate = await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-REVOKE", ["View"]));
            mandateId = mandate.MandateId;
        }

        await using var ctx2 = fixture.CreateContext();
        var response = await new MandateService(ctx2, TestAudit.For(ctx2), new TestCaller("revoking-client"))
            .RevokeMandateAsync(mandateId, new RevokeMandateRequest("customer requested removal"));

        Assert.Equal("Revoked", response.Status);
        Assert.Equal("revoking-client", response.RevokedBy);
        Assert.Equal("customer requested removal", response.RevocationReason);
        Assert.NotNull(response.RevokedAt);

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.MandateRevoked);
        Assert.Equal(mandateId, audit.EntityId);
    }

    [Fact]
    public async Task RevokeMandateAsync_IsIdempotent_WhenAlreadyRevoked()
    {
        var customerId = await CreateCustomerAsync();
        string mandateId;
        await using (var ctx = fixture.CreateContext())
        {
            var mandate = await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-REVOKE-TWICE", ["View"]));
            mandateId = mandate.MandateId;
        }

        await using (var ctx = fixture.CreateContext())
        {
            await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .RevokeMandateAsync(mandateId, new RevokeMandateRequest("first revoke"));
        }

        await using var ctx2 = fixture.CreateContext();
        var response = await new MandateService(ctx2, TestAudit.For(ctx2), TestCaller.Default)
            .RevokeMandateAsync(mandateId, new RevokeMandateRequest("second revoke"));

        Assert.Equal("Revoked", response.Status);
        Assert.Equal("first revoke", response.RevocationReason);

        await using var verifyCtx = fixture.CreateContext();
        // Only the first call wrote an audit row - the idempotent repeat is a pure read, same pattern as
        // CustomerService's suspend/reactivate idempotency.
        Assert.Equal(1, await verifyCtx.AuditEvents.CountAsync(a => a.Action == AuditActions.MandateRevoked));
    }

    [Fact]
    public async Task RevokeMandateAsync_Throws_ForAnUnknownMandate()
    {
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<MandateNotFoundException>(() =>
            new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .RevokeMandateAsync("MND-DOESNOTEXIST", new RevokeMandateRequest("n/a")));
    }
}
