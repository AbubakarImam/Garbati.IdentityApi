using System.Text.Json;
using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Outbox;
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

    private static CreateCustomerRequest NewCustomerRequest(string fullName = "Ada Lovelace", string idempotencyKey = "test-create-customer") =>
        new(fullName, "NG", CustomerReference: null, Email: "ada@example.com", PhoneNumber: "+2348000000000", IdempotencyKey: idempotencyKey);

    private async Task<string> CreateCustomerAsync()
    {
        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .CreateCustomerAsync(NewCustomerRequest(idempotencyKey: Guid.NewGuid().ToString()));
        return response.CustomerId;
    }

    [Fact]
    public async Task CreateCustomerAsync_CreatesAnActivePendingCustomer()
    {
        await using var ctx = fixture.CreateContext();
        var service = new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default);

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
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).CreateCustomerAsync(NewCustomerRequest());

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
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).CreateCustomerAsync(NewCustomerRequest());

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
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).GetCustomerAsync("CUS-DOESNOTEXIST");

        Assert.Null(response);
    }

    [Fact]
    public async Task GetCustomerAsync_ReturnsTheRedactedView_WhenCustomerExists()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).GetCustomerAsync(customerId);

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
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).RecordKycDecisionAsync(customerId, request);

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
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).RecordKycDecisionAsync(customerId, request);

        Assert.Equal("Rejected", response.KycStatus);
        Assert.Null(response.KycTier);
    }

    [Fact]
    public async Task RecordKycDecisionAsync_Throws_ForAnUnknownCustomer()
    {
        var request = new KycDecisionRequest("Verified", "Tier1", "BVN", "test-provider", null, "OK", null);

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).RecordKycDecisionAsync("CUS-DOESNOTEXIST", request));
    }

    [Fact]
    public async Task RecordKycDecisionAsync_Throws_WhenCustomerIsClosed()
    {
        var customerId = await CreateCustomerAsync();
        await using (var closeCtx = fixture.CreateContext())
        {
            await new CustomerService(closeCtx, TestAudit.For(closeCtx), TestCaller.Default)
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("account closed by customer"));
        }

        var request = new KycDecisionRequest("Verified", "Tier1", "BVN", "test-provider", null, "OK", null);
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).RecordKycDecisionAsync(customerId, request));
    }

    [Fact]
    public async Task SuspendCustomerAsync_MovesActiveToSuspended_AndRecordsAnAuditEvent()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
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
            await new CustomerService(ctx1, TestAudit.For(ctx1), TestCaller.Default)
                .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("first suspension"));
        }

        await using var ctx2 = fixture.CreateContext();
        var response = await new CustomerService(ctx2, TestAudit.For(ctx2), TestCaller.Default)
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
            await new CustomerService(closeCtx, TestAudit.For(closeCtx), TestCaller.Default)
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("closed"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("too late")));
    }

    [Fact]
    public async Task ReactivateCustomerAsync_MovesSuspendedToActive()
    {
        var customerId = await CreateCustomerAsync();
        await using (var suspendCtx = fixture.CreateContext())
        {
            await new CustomerService(suspendCtx, TestAudit.For(suspendCtx), TestCaller.Default)
                .SuspendCustomerAsync(customerId, new CustomerStatusChangeRequest("review"));
        }

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .ReactivateCustomerAsync(customerId, new CustomerStatusChangeRequest("review cleared"));

        Assert.Equal("Active", response.CustomerStatus);

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.CustomerReactivated);
        Assert.Contains("review cleared", audit.Details);
    }

    [Fact]
    public async Task ReactivateCustomerAsync_IsIdempotent_WhenAlreadyActive()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .ReactivateCustomerAsync(customerId, new CustomerStatusChangeRequest(null));

        Assert.Equal("Active", response.CustomerStatus);
    }

    [Fact]
    public async Task ReactivateCustomerAsync_Throws_WhenCustomerIsDeceased()
    {
        var customerId = await CreateCustomerAsync();
        await using (var deceasedCtx = fixture.CreateContext())
        {
            await new CustomerService(deceasedCtx, TestAudit.For(deceasedCtx), TestCaller.Default)
                .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("confirmed deceased"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .ReactivateCustomerAsync(customerId, new CustomerStatusChangeRequest("mistake")));
    }

    [Fact]
    public async Task CloseCustomerAsync_MovesActiveToClosed_AndRecordsAnAuditEvent()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("customer requested closure"));

        Assert.Equal("Closed", response.CustomerStatus);

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.CustomerClosed);
        Assert.Equal(customerId, audit.EntityId);
        Assert.Contains("customer requested closure", audit.Details);
    }

    // DESIGN.md Section 2.1, decision #14: the task file leaves "409 or no-op" as a choice for a
    // repeated Closed/Deceased attempt. 409 was chosen (see InvalidCustomerStatusTransitionException).
    [Fact]
    public async Task CloseCustomerAsync_Throws_WhenCustomerIsAlreadyClosed()
    {
        var customerId = await CreateCustomerAsync();
        await using (var closeCtx = fixture.CreateContext())
        {
            await new CustomerService(closeCtx, TestAudit.For(closeCtx), TestCaller.Default)
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("first closure"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("second closure")));
    }

    [Fact]
    public async Task MarkDeceasedAsync_MovesActiveToDeceased_AndRecordsAnAuditEvent()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("death certificate received"));

        Assert.Equal("Deceased", response.CustomerStatus);

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.CustomerMarkedDeceased);
        Assert.Equal(customerId, audit.EntityId);
        Assert.Contains("death certificate received", audit.Details);
    }

    [Fact]
    public async Task MarkDeceasedAsync_Throws_WhenCustomerIsAlreadyDeceased()
    {
        var customerId = await CreateCustomerAsync();
        await using (var deceasedCtx = fixture.CreateContext())
        {
            await new CustomerService(deceasedCtx, TestAudit.For(deceasedCtx), TestCaller.Default)
                .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("first notice"));
        }

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidCustomerStatusTransitionException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("second notice")));
    }

    // T-016 left this test asserting zero outbox rows, specifically because Mandates didn't exist yet -
    // and specifically written to force T-017 to come back and update it once they do (see its original
    // comment in the T-016 PR). This is that update: a customer with no mandates at all still writes
    // nothing, which is still real (not faked) behaviour, just no longer the *only* behaviour this path
    // can produce - see the tests below for the cases that now write something.
    [Fact]
    public async Task CloseCustomerAsync_WritesNoOutboxMessage_WhenTheCustomerHasNoMandates()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("closure"));

        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(0, await verifyCtx.OutboxMessages.CountAsync());
    }

    // DESIGN.md Section 2.1/2.5, decision #14, now wired up for real (T-017): closing a customer who was
    // the SOLE active holder of an account revokes their mandate and queues exactly one freeze message
    // naming that account.
    [Fact]
    public async Task CloseCustomerAsync_RevokesTheMandate_AndQueuesOneFreeze_WhenTheCustomerWasTheSoleHolder()
    {
        var customerId = await CreateCustomerAsync();
        await using (var attachCtx = fixture.CreateContext())
        {
            await new MandateService(attachCtx, TestAudit.For(attachCtx), TestCaller.Default)
                .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-SOLE-HOLDER", ["Debit"]));
        }

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .CloseCustomerAsync(customerId, new CustomerStatusChangeRequest("customer requested closure"));

        await using var verifyCtx = fixture.CreateContext();
        var mandate = await verifyCtx.Mandates.SingleAsync(m => m.LedgerAccountId == "ACC-SOLE-HOLDER");
        Assert.Equal(MandateStatus.Revoked, mandate.Status);
        Assert.NotNull(mandate.RevokedAt);

        var outboxMessage = await verifyCtx.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxMessageTypes.AccountFreezeRequested, outboxMessage.MessageType);
        var payload = JsonSerializer.Deserialize<AccountFreezeRequestedPayload>(outboxMessage.Payload)!;
        Assert.Equal("ACC-SOLE-HOLDER", payload.LedgerAccountId);
        Assert.Equal(customerId, payload.CustomerId);
    }

    // A joint account with a surviving active holder is NOT frozen (DESIGN.md Section 2.1's table) -
    // only the closed customer's own mandate is revoked.
    [Fact]
    public async Task CloseCustomerAsync_RevokesOnlyItsOwnMandate_AndQueuesNoFreeze_WhenAnotherHolderStaysActive()
    {
        var closingCustomerId = await CreateCustomerAsync();

        string otherCustomerId;
        await using (var otherCtx = fixture.CreateContext())
        {
            var response = await new CustomerService(otherCtx, TestAudit.For(otherCtx), new TestCaller("other-client"))
                .CreateCustomerAsync(NewCustomerRequest(fullName: "Grace Hopper", idempotencyKey: Guid.NewGuid().ToString()));
            otherCustomerId = response.CustomerId;
        }

        await using (var attachCtx = fixture.CreateContext())
        {
            var mandateService = new MandateService(attachCtx, TestAudit.For(attachCtx), TestCaller.Default);
            await mandateService.AttachMandateAsync(closingCustomerId, new AttachMandateRequest("ACC-JOINT-HOLDER", ["Debit"]));
            await mandateService.AttachMandateAsync(otherCustomerId, new AttachMandateRequest("ACC-JOINT-HOLDER", ["Debit"]));
        }

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .CloseCustomerAsync(closingCustomerId, new CustomerStatusChangeRequest("customer requested closure"));

        await using var verifyCtx = fixture.CreateContext();
        var closingCustomerPk = await verifyCtx.Customers.Where(c => c.CustomerId == closingCustomerId).Select(c => c.Id).SingleAsync();
        var otherCustomerPk = await verifyCtx.Customers.Where(c => c.CustomerId == otherCustomerId).Select(c => c.Id).SingleAsync();

        var closedCustomerMandate = await verifyCtx.Mandates.SingleAsync(
            m => m.LedgerAccountId == "ACC-JOINT-HOLDER" && m.CustomerId == closingCustomerPk);
        Assert.Equal(MandateStatus.Revoked, closedCustomerMandate.Status);

        var otherCustomerMandate = await verifyCtx.Mandates.SingleAsync(
            m => m.LedgerAccountId == "ACC-JOINT-HOLDER" && m.CustomerId == otherCustomerPk);
        Assert.Equal(MandateStatus.Active, otherCustomerMandate.Status);

        Assert.Equal(0, await verifyCtx.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task MarkDeceasedAsync_RevokesTheMandate_AndQueuesOneFreeze_WhenTheCustomerWasTheSoleHolder()
    {
        var customerId = await CreateCustomerAsync();
        await using (var attachCtx = fixture.CreateContext())
        {
            await new MandateService(attachCtx, TestAudit.For(attachCtx), TestCaller.Default)
                .AttachMandateAsync(customerId, new AttachMandateRequest("ACC-DECEASED-SOLE", ["Debit"]));
        }

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .MarkDeceasedAsync(customerId, new CustomerStatusChangeRequest("death certificate received"));

        await using var verifyCtx = fixture.CreateContext();
        var mandate = await verifyCtx.Mandates.SingleAsync(m => m.LedgerAccountId == "ACC-DECEASED-SOLE");
        Assert.Equal(MandateStatus.Revoked, mandate.Status);
        Assert.Equal(1, await verifyCtx.OutboxMessages.CountAsync(o => o.MessageType == OutboxMessageTypes.AccountFreezeRequested));
    }

    // T-035 (garbati-platform D-19): PATCH /v1/customers/{customerId}/details.
    [Fact]
    public async Task UpdateDetailsAsync_UpdatesOnlyTheSuppliedField_AndLeavesTheOthersUnchanged()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .UpdateDetailsAsync(customerId, new UpdateCustomerDetailsRequest("Ada Updated", null, null));

        await using var verifyCtx = fixture.CreateContext();
        var customer = await verifyCtx.Customers.SingleAsync(c => c.CustomerId == customerId);
        var identityData = await verifyCtx.CustomerIdentityData.SingleAsync(d => d.CustomerId == customer.Id);
        Assert.Equal("Ada Updated", identityData.FullName);
        // Unchanged - the original NewCustomerRequest() values from CreateCustomerAsync's helper above.
        Assert.Equal("ada@example.com", identityData.Email);
        Assert.Equal("+2348000000000", identityData.PhoneNumber);
    }

    [Fact]
    public async Task UpdateDetailsAsync_UpdatesAllThreeFields_WhenAllThreeAreSupplied()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .UpdateDetailsAsync(customerId, new UpdateCustomerDetailsRequest(
                "Grace Hopper", "grace@example.com", "+2348000000099"));

        await using var verifyCtx = fixture.CreateContext();
        var customer = await verifyCtx.Customers.SingleAsync(c => c.CustomerId == customerId);
        var identityData = await verifyCtx.CustomerIdentityData.SingleAsync(d => d.CustomerId == customer.Id);
        Assert.Equal("Grace Hopper", identityData.FullName);
        Assert.Equal("grace@example.com", identityData.Email);
        Assert.Equal("+2348000000099", identityData.PhoneNumber);
    }

    [Fact]
    public async Task UpdateDetailsAsync_RecordsAnAuditRow_WithOldAndNewValuesForOnlyTheSuppliedFields()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .UpdateDetailsAsync(customerId, new UpdateCustomerDetailsRequest(null, "new-email@example.com", null));

        await using var verifyCtx = fixture.CreateContext();
        var audit = await verifyCtx.AuditEvents.SingleAsync(a => a.Action == AuditActions.CustomerDetailsUpdated);
        Assert.Equal(nameof(Customer), audit.EntityType);
        Assert.Equal(customerId, audit.EntityId);
        // Only Email was supplied - FullName/PhoneNumber must not appear in the audit details at all.
        Assert.Contains("Email ada@example.com -> new-email@example.com", audit.Details);
        Assert.DoesNotContain("FullName", audit.Details);
        Assert.DoesNotContain("PhoneNumber", audit.Details);
    }

    [Fact]
    public async Task UpdateDetailsAsync_Throws_ForAnUnknownCustomer()
    {
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<CustomerNotFoundException>(() =>
            new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default)
                .UpdateDetailsAsync("CUS-DOESNOTEXIST", new UpdateCustomerDetailsRequest("New Name", null, null)));
    }

    [Theory]
    [InlineData("suspend")]
    [InlineData("reactivate")]
    [InlineData("close")]
    [InlineData("mark-deceased")]
    public async Task StatusChangeMethods_Throw_ForAnUnknownCustomer(string operation)
    {
        await using var ctx = fixture.CreateContext();
        var service = new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default);
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

    // Idempotency on customer creation (decision #29), mirroring LedgerApi's AccountServiceTests for
    // POST /api/accounts (decision #43) - same mechanics, same test shapes.

    // Guards the orchestrator-retry scenario: a timed-out create, retried with the same idempotency key
    // and the same body, must return the customer it already created instead of a second one.
    [Fact]
    public async Task CreateCustomerAsync_RetriedWithTheSameKeyAndBody_ReturnsTheSameCustomer_AndCreatesOnlyOne()
    {
        var request = NewCustomerRequest(idempotencyKey: "order-42");

        await using var ctx1 = fixture.CreateContext();
        var first = await new CustomerService(ctx1, TestAudit.For(ctx1), TestCaller.Default).CreateCustomerAsync(request);

        await using var ctx2 = fixture.CreateContext();
        var retry = await new CustomerService(ctx2, TestAudit.For(ctx2), TestCaller.Default).CreateCustomerAsync(request);

        Assert.Equal(first.CustomerId, retry.CustomerId);

        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(1, await verifyCtx.Customers.CountAsync());
    }

    [Theory]
    [InlineData("Other Name", "ada@example.com", "+2348000000000")]
    [InlineData("Ada Lovelace", "someone-else@example.com", "+2348000000000")]
    [InlineData("Ada Lovelace", "ada@example.com", "+2348000000001")]
    public async Task CreateCustomerAsync_ReusedKeyForADifferentRequest_Throws409_AndCreatesOnlyOne(
        string fullName, string email, string phoneNumber)
    {
        await using var ctx1 = fixture.CreateContext();
        await new CustomerService(ctx1, TestAudit.For(ctx1), TestCaller.Default).CreateCustomerAsync(
            new CreateCustomerRequest("Ada Lovelace", "NG", null, "ada@example.com", "+2348000000000", "order-42"));

        await using var ctx2 = fixture.CreateContext();
        await Assert.ThrowsAsync<DuplicateIdempotencyKeyException>(() =>
            new CustomerService(ctx2, TestAudit.For(ctx2), TestCaller.Default).CreateCustomerAsync(
                new CreateCustomerRequest(fullName, "NG", null, email, phoneNumber, "order-42")));

        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(1, await verifyCtx.Customers.CountAsync());
    }

    [Fact]
    public async Task CreateCustomerAsync_SameKeyFromTwoClients_CreatesTwoSeparateCustomers()
    {
        var request = NewCustomerRequest(idempotencyKey: "order-42");

        await using var ctx1 = fixture.CreateContext();
        var fromA = await new CustomerService(ctx1, TestAudit.For(ctx1), new TestCaller("client-a")).CreateCustomerAsync(request);

        await using var ctx2 = fixture.CreateContext();
        var fromB = await new CustomerService(ctx2, TestAudit.For(ctx2), new TestCaller("client-b")).CreateCustomerAsync(request);

        Assert.NotEqual(fromA.CustomerId, fromB.CustomerId);
    }

    [Fact]
    public async Task CreateCustomerAsync_ConcurrentRequestsWithOneKeyAndDifferentBodies_OneSucceedsOneConflicts()
    {
        var attempts = new[] { "ada@example.com", "someone-else@example.com" }.Select(email => Task.Run(async () =>
        {
            await using var ctx = fixture.CreateContext();
            try
            {
                await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).CreateCustomerAsync(
                    new CreateCustomerRequest("Ada Lovelace", "NG", null, email, "+2348000000000", "key-1"));
                return "ok";
            }
            catch (DuplicateIdempotencyKeyException)
            {
                return "conflict";
            }
        }));

        var results = await Task.WhenAll(attempts);

        Assert.Single(results, r => r == "ok");
        Assert.Single(results, r => r == "conflict");
        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(1, await verifyCtx.Customers.CountAsync());
    }

    // The database's unique constraint, not application logic, is what guarantees this: two requests
    // racing with the same key and the same body must still create exactly one customer between them.
    [Fact]
    public async Task CreateCustomerAsync_ConcurrentRequestsWithTheSameKeyAndSameBody_CreatesExactlyOneCustomer()
    {
        var request = NewCustomerRequest(idempotencyKey: "order-42");

        var attempts = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await using var ctx = fixture.CreateContext();
            var response = await new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default).CreateCustomerAsync(request);
            return response.CustomerId;
        }));

        var customerIds = await Task.WhenAll(attempts);

        Assert.Equal(customerIds[0], customerIds[1]);
        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(1, await verifyCtx.Customers.CountAsync());
    }
}
