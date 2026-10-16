using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Contracts.Responses;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;

namespace Garbati.IdentityApi.Tests.Services;

[Collection("Postgres")]
public class CustomerServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static CreateCustomerRequest NewCustomerRequest(string fullName = "Ada Lovelace") =>
        new(fullName, "NG", CustomerReference: null, Email: "ada@example.com", PhoneNumber: "+2348000000000");

    private async Task<string> CreateCustomerAsync()
    {
        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx)).CreateCustomerAsync(NewCustomerRequest());
        return response.CustomerId;
    }

    [Fact]
    public async Task CreateCustomerAsync_CreatesAnActivePendingCustomer()
    {
        await using var ctx = fixture.CreateContext();
        var service = new CustomerService(ctx, TestAudit.For(ctx));

        var response = await service.CreateCustomerAsync(NewCustomerRequest());

        Assert.StartsWith("CUS-", response.CustomerId);
        Assert.Equal("Active", response.CustomerStatus);
        Assert.Equal("Pending", response.KycStatus);
        Assert.Null(response.KycTier);
        Assert.Equal("NG", response.JurisdictionCode);
    }

    [Fact]
    public async Task CreateCustomerAsync_StoresIdentityDataSeparately_FromTheCustomerRecord()
    {
        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx)).CreateCustomerAsync(NewCustomerRequest());

        await using var verifyCtx = fixture.CreateContext();
        var customer = await verifyCtx.Customers.SingleAsync(c => c.CustomerId == response.CustomerId);
        var identityData = await verifyCtx.CustomerIdentityData.SingleAsync(d => d.CustomerId == customer.Id);
        Assert.Equal("Ada Lovelace", identityData.FullName);
        Assert.Equal("ada@example.com", identityData.Email);
    }

    [Fact]
    public async Task CreateCustomerAsync_RecordsAuditRow()
    {
        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx)).CreateCustomerAsync(NewCustomerRequest());

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync();
        Assert.Equal(AuditActions.CustomerCreated, audit.Action);
        Assert.Equal(nameof(Customer), audit.EntityType);
        Assert.Equal(response.CustomerId, audit.EntityId);
        Assert.Equal("Active", audit.Status);
    }

    [Fact]
    public async Task GetCustomerAsync_ReturnsNull_WhenCustomerDoesNotExist()
    {
        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx)).GetCustomerAsync("CUS-DOESNOTEXIST");

        Assert.Null(response);
    }

    [Fact]
    public async Task GetCustomerAsync_ReturnsTheRedactedView_WhenCustomerExists()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx)).GetCustomerAsync(customerId);

        Assert.NotNull(response);
        Assert.Equal(customerId, response!.CustomerId);
        // CustomerResponse has no FullName/Email/PhoneNumber property at all - nothing from
        // CustomerIdentityData can leak into it (DESIGN.md Section 6.2). Reflection proves this holds
        // even if someone edits the record later without checking this test.
        var propertyNames = typeof(CustomerResponse).GetProperties().Select(p => p.Name);
        Assert.DoesNotContain("FullName", propertyNames);
        Assert.DoesNotContain("Email", propertyNames);
        Assert.DoesNotContain("PhoneNumber", propertyNames);
    }

    [Fact]
    public async Task RecordKycDecisionAsync_Verified_UpdatesStatusAndTier_AndRecordsAVerificationRow()
    {
        var customerId = await CreateCustomerAsync();
        var request = new KycDecisionRequest("Verified", "Tier1", "BVN", "test-provider", "ref-1", "OK", null);

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx)).RecordKycDecisionAsync(customerId, request);

        Assert.Equal("Verified", response.KycStatus);
        Assert.Equal("Tier1", response.KycTier);

        await using var verifyCtx = fixture.CreateContext();
        var verification = await verifyCtx.KycVerifications.SingleAsync();
        Assert.Equal("BVN", verification.VerificationType);
        Assert.Equal(KycStatus.Verified, verification.Status);
        Assert.NotNull(verification.VerifiedAt);

        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.KycDecisionRecorded);
        Assert.Equal(customerId, audit.EntityId);
    }

    [Fact]
    public async Task RecordKycDecisionAsync_Rejected_DoesNotSetATier()
    {
        var customerId = await CreateCustomerAsync();
        var request = new KycDecisionRequest("Rejected", null, "BVN", "test-provider", null, "MISMATCH", null);

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx)).RecordKycDecisionAsync(customerId, request);

        Assert.Equal("Rejected", response.KycStatus);
        Assert.Null(response.KycTier);
    }

    [Fact]
    public async Task RecordKycDecisionAsync_Throws_ForAnUnknownCustomer()
    {
        var request = new KycDecisionRequest("Verified", "Tier1", "BVN", "test-provider", null, "OK", null);

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx)).RecordKycDecisionAsync("CUS-DOESNOTEXIST", request));
    }

    [Fact]
    public async Task RecordKycDecisionAsync_Throws_WhenCustomerIsClosed()
    {
        var customerId = await CreateCustomerAsync();
        await using (var closeCtx = fixture.CreateContext())
        {
            await new CustomerService(closeCtx, TestAudit.For(closeCtx))
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("account closed by customer"));
        }

        var request = new KycDecisionRequest("Verified", "Tier1", "BVN", "test-provider", null, "OK", null);
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx)).RecordKycDecisionAsync(customerId, request));
    }

    [Fact]
    public async Task SuspendCustomerAsync_MovesActiveToSuspended_AndRecordsAnAuditEvent()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx))
            .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("fraud review"));

        Assert.Equal("Suspended", response.CustomerStatus);

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.CustomerSuspended);
        Assert.Equal(customerId, audit.EntityId);
        Assert.Contains("fraud review", audit.Details);
    }

    [Fact]
    public async Task SuspendCustomerAsync_IsIdempotent_WhenAlreadySuspended()
    {
        var customerId = await CreateCustomerAsync();
        await using (var ctx1 = fixture.CreateContext())
        {
            await new CustomerService(ctx1, TestAudit.For(ctx1))
                .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("first suspension"));
        }

        await using var ctx2 = fixture.CreateContext();
        var response = await new CustomerService(ctx2, TestAudit.For(ctx2))
            .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("second call"));

        Assert.Equal("Suspended", response.CustomerStatus);

        await using var verifyCtx = fixture.CreateContext();
        // Only the first call wrote an audit row; the idempotent repeat is a pure read.
        Assert.Equal(1, await verifyCtx.AuditEvents.CountAsync(a => a.Action == AuditActions.CustomerSuspended));
    }

    [Fact]
    public async Task SuspendCustomerAsync_Throws_WhenCustomerIsClosed()
    {
        var customerId = await CreateCustomerAsync();
        await using (var closeCtx = fixture.CreateContext())
        {
            await new CustomerService(closeCtx, TestAudit.For(closeCtx))
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("closed"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx))
                .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("too late")));
    }

    [Fact]
    public async Task ReactivateCustomerAsync_MovesSuspendedToActive()
    {
        var customerId = await CreateCustomerAsync();
        await using (var suspendCtx = fixture.CreateContext())
        {
            await new CustomerService(suspendCtx, TestAudit.For(suspendCtx))
                .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("review"));
        }

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx))
            .ReactivateCustomerAsync(customerId, new CustomerStatusChangeRequest("review cleared"));

        Assert.Equal("Active", response.CustomerStatus);
    }

    [Fact]
    public async Task ReactivateCustomerAsync_IsIdempotent_WhenAlreadyActive()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx))
            .ReactivateCustomerAsync(customerId, new CustomerStatusChangeRequest(null));

        Assert.Equal("Active", response.CustomerStatus);
    }

    [Fact]
    public async Task ReactivateCustomerAsync_Throws_WhenCustomerIsDeceased()
    {
        var customerId = await CreateCustomerAsync();
        await using (var deceasedCtx = fixture.CreateContext())
        {
            await new CustomerService(deceasedCtx, TestAudit.For(deceasedCtx))
                .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("confirmed deceased"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx))
                .ReactivateCustomerAsync(customerId, new CustomerStatusChangeRequest("mistake")));
    }

    [Fact]
    public async Task CloseCustomerAsync_MovesActiveToClosed_AndRecordsAnAuditEvent()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx))
            .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("customer requested closure"));

        Assert.Equal("Closed", response.CustomerStatus);

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.CustomerClosed);
        Assert.Equal(customerId, audit.EntityId);
    }

    // DESIGN.md Section 2.1, decision #14: the task file leaves "409 or no-op" as a choice for a
    // repeated Closed/Deceased attempt. 409 was chosen (see InvalidCustomerStatusTransitionException).
    [Fact]
    public async Task CloseCustomerAsync_Throws_WhenCustomerIsAlreadyClosed()
    {
        var customerId = await CreateCustomerAsync();
        await using (var closeCtx = fixture.CreateContext())
        {
            await new CustomerService(closeCtx, TestAudit.For(closeCtx))
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("first closure"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx))
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("second closure")));
    }

    [Fact]
    public async Task MarkDeceasedAsync_MovesActiveToDeceased_AndRecordsAnAuditEvent()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx))
            .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("death certificate received"));

        Assert.Equal("Deceased", response.CustomerStatus);

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.CustomerMarkedDeceased);
        Assert.Equal(customerId, audit.EntityId);
    }

    [Fact]
    public async Task MarkDeceasedAsync_Throws_WhenCustomerIsAlreadyDeceased()
    {
        var customerId = await CreateCustomerAsync();
        await using (var deceasedCtx = fixture.CreateContext())
        {
            await new CustomerService(deceasedCtx, TestAudit.For(deceasedCtx))
                .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("first notice"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx))
                .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("second notice")));
    }

    // The task file is explicit: T-016 must not fake a mandate lookup. This test documents today's real
    // behaviour (no OutboxMessage row) so it fails loudly, as a reminder, the day T-017 wires the cascade
    // in and this test needs to be updated alongside it rather than silently going stale.
    [Fact]
    public async Task CloseCustomerAsync_WritesNoOutboxMessage_BecauseMandatesDoNotExistYet()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx))
            .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("closure"));

        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(0, await verifyCtx.OutboxMessages.CountAsync());
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    [InlineData("close")]
    [InlineData("mark-deceased")]
    public async Task StatusChangeMethods_Throw_ForAnUnknownCustomer(string operation)
    {
        await using var ctx = fixture.CreateContext();
        var service = new CustomerService(ctx, TestAudit.For(ctx));
        var request = new CustomerStatusChangeRequest("n/a");

        Task Act() => operation switch
        {
            "suspend" => service.SuspendCustomerAsync("CUS-DOESNOTEXIST", request),
            "reactivate" => service.ReactivateCustomerAsync("CUS-DOESNOTEXIST", request),
            "close" => service.CloseCustomerAsync("CUS-DOESNOTEXIST", request),
            _ => service.MarkDeceasedAsync("CUS-DOESNOTEXIST", request),
        };

        await Assert.ThrowsAsync<CustomerNotFoundException>(Act);
    }
}
