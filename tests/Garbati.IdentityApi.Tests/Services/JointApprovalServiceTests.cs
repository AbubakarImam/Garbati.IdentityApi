using Garbati.IdentityApi.Auditing;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Signing;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Garbati.IdentityApi.Tests.Services;

// DESIGN.md Section 3.8 and the T-018 task file's own "Tests" list. Covers the joint-account
// multi-approval workflow: collecting approvals (3.8.1), the 1-hour timeout (3.8.3), threshold
// reconfiguration (3.8.4), and the two halves of decision #24 (eager void on revoke, live re-check at
// threshold-completion). AuthorizationServiceTests.cs (T-017) is untouched - the RequiredApprovals == 1
// path's behavior and tests are not part of this file.
[Collection("Postgres")]
public class JointApprovalServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IGrantSigner CreateGrantSigner() => new GrantSigner(Options.Create(new SigningKeyOptions
    {
        ActiveKid = TestSigningKey.Kid,
        Keys = [new SigningKeyEntry { Kid = TestSigningKey.Kid, PrivateKeyPem = TestSigningKey.PrivateKeyPem }],
    }));

    private static JointApprovalService CreateJointApprovalService(Garbati.IdentityApi.Data.IdentityDbContext ctx) =>
        new(ctx, TestAudit.For(ctx), new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner()));

    private async Task<string> CreateCustomerAsync(string fullName, string? kycTier = "Tier2")
    {
        await using var ctx = fixture.CreateContext();
        var customerService = new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default);
        var response = await customerService.CreateCustomerAsync(
            new CreateCustomerRequest(fullName, "NG", null, null, null, Guid.NewGuid().ToString()));

        if (kycTier is not null)
        {
            await customerService.RecordKycDecisionAsync(
                response.CustomerId, new KycDecisionRequest("Verified", kycTier, "BVN", "test-provider", null, "OK", null));
        }

        return response.CustomerId;
    }

    private async Task<string> AttachMandateAsync(string customerId, string ledgerAccountId, params string[] operations)
    {
        await using var ctx = fixture.CreateContext();
        var mandate = await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .AttachMandateAsync(customerId, new AttachMandateRequest(ledgerAccountId, operations.ToList()));
        return mandate.MandateId;
    }

    private async Task RevokeMandateAsync(string mandateId, string reason = "test revoke")
    {
        await using var ctx = fixture.CreateContext();
        await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .RevokeMandateAsync(mandateId, new RevokeMandateRequest(reason));
    }

    private static AuthorizationRequest DebitRequest(string customerId, string ledgerAccountId, decimal amount = 5000m) =>
        new(customerId, "Debit", ledgerAccountId, "ACC-DEST", amount, "NGN", $"idem-{Guid.NewGuid()}");

    // Raises an account's threshold from its default of 1 using the real reconfiguration flow itself
    // (3.8.4) - the first id in holderCustomerIds is the requester, every other id then approves.
    private async Task<string> SetRequiredApprovalsAsync(string ledgerAccountId, int requiredApprovals, params string[] holderCustomerIds)
    {
        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var response = await CreateJointApprovalService(ctx).RequestThresholdChangeAsync(
                ledgerAccountId, new ChangeApprovalPolicyRequest(holderCustomerIds[0], requiredApprovals));
            requestId = response.RequestId;
        }

        foreach (var holder in holderCustomerIds.Skip(1))
        {
            await using var ctx = fixture.CreateContext();
            await CreateJointApprovalService(ctx).ApproveAsync(requestId, new ApproveRequest(holder));
        }

        return requestId;
    }

    // --- RequestDebitAsync branching -----------------------------------------------------------------

    [Fact]
    public async Task RequestDebitAsync_ReturnsPending_ForAMultiHolderAccount_WithTheRequestersOwnApprovalCounted()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var customerC = await CreateCustomerAsync("Holder C");
        await AttachMandateAsync(customerA, "ACC-2OF3", "Debit");
        await AttachMandateAsync(customerB, "ACC-2OF3", "Debit");
        await AttachMandateAsync(customerC, "ACC-2OF3", "Debit");
        await SetRequiredApprovalsAsync("ACC-2OF3", 2, customerA, customerB, customerC);

        await using var ctx = fixture.CreateContext();
        var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-2OF3"));

        var pending = Assert.IsType<DebitRequestResult.Pending>(result).Response;
        Assert.Equal("Pending", pending.Status);
        Assert.Equal("Debit", pending.Kind);
        Assert.Equal(2, pending.RequiredApprovals);
        Assert.Equal(1, pending.ApprovalCount); // the requester's own approval, counted immediately
        Assert.Null(pending.DecisionId);
        Assert.Null(pending.Grant);
    }

    [Fact]
    public async Task RequestDebitAsync_StillGrantsImmediately_ForASingleHolderAccount()
    {
        var customerId = await CreateCustomerAsync("Solo Holder");
        await AttachMandateAsync(customerId, "ACC-SOLO", "Debit");

        await using var ctx = fixture.CreateContext();
        var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerId, "ACC-SOLO"));

        var granted = Assert.IsType<DebitRequestResult.Granted>(result).Response;
        Assert.True(granted.Decision.Approved);
        Assert.NotEmpty(granted.Grant);
    }

    [Fact]
    public async Task RequestDebitAsync_Throws_WhenTheInitiatorHasNoActiveMandateOnTheAccount()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var outsider = await CreateCustomerAsync("Outsider");
        await AttachMandateAsync(customerA, "ACC-OUTSIDER", "Debit");
        await AttachMandateAsync(customerB, "ACC-OUTSIDER", "Debit");
        await SetRequiredApprovalsAsync("ACC-OUTSIDER", 2, customerA, customerB);

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(outsider, "ACC-OUTSIDER")));

        Assert.Equal(AuthorizationDenialReasons.NoActiveMandate, ex.ReasonCode);

        // No Debit-kind request was created for the outsider - the only PendingApprovalRequest on this
        // account is the ThresholdChange one SetRequiredApprovalsAsync itself created during setup.
        await using var verifyCtx = fixture.CreateContext();
        Assert.Equal(0, await verifyCtx.PendingApprovalRequests.CountAsync(
            r => r.LedgerAccountId == "ACC-OUTSIDER" && r.Kind == PendingApprovalRequestKind.Debit));
    }

    // --- Task file test: "A 2-of-3 account: one approval isn't enough; a second distinct holder's
    // approval completes it and issues a grant; the requester's own approval correctly counts as the
    // first." ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TwoOfThreeAccount_OneApprovalIsNotEnough_ASecondDistinctHolderCompletesItAndIssuesAGrant()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var customerC = await CreateCustomerAsync("Holder C");
        await AttachMandateAsync(customerA, "ACC-2OF3B", "Debit");
        var mandateB = await AttachMandateAsync(customerB, "ACC-2OF3B", "Debit");
        await AttachMandateAsync(customerC, "ACC-2OF3B", "Debit");
        await SetRequiredApprovalsAsync("ACC-2OF3B", 2, customerA, customerB, customerC);
        _ = mandateB;

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-2OF3B"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        // Still only the requester's own approval - not enough.
        await using (var ctx = fixture.CreateContext())
        {
            var still = await CreateJointApprovalService(ctx).GetPendingRequestAsync(requestId);
            Assert.Equal("Pending", still!.Status);
            Assert.Equal(1, still.ApprovalCount);
        }

        // Holder B (a different holder) approves - this is the 2nd of 2 required, so it completes.
        await using var approveCtx = fixture.CreateContext();
        var response = await CreateJointApprovalService(approveCtx).ApproveAsync(requestId, new ApproveRequest(customerB));

        Assert.Equal("Approved", response.Status);
        Assert.Equal(2, response.ApprovalCount);
        Assert.NotNull(response.DecisionId);
        Assert.NotNull(response.Grant);
        Assert.NotEmpty(response.Grant!);
    }

    // --- Task file test: "A 2-of-2 account: the requester's own approval is 1 of 2, a second holder
    // completes it." ----------------------------------------------------------------------------------

    [Fact]
    public async Task TwoOfTwoAccount_RequestersOwnApprovalIsOneOfTwo_ASecondHolderCompletesIt()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-2OF2", "Debit");
        await AttachMandateAsync(customerB, "ACC-2OF2", "Debit");
        await SetRequiredApprovalsAsync("ACC-2OF2", 2, customerA, customerB);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-2OF2"));
            var pending = Assert.IsType<DebitRequestResult.Pending>(result).Response;
            requestId = pending.RequestId;
            Assert.Equal(1, pending.ApprovalCount);
        }

        await using var approveCtx = fixture.CreateContext();
        var response = await CreateJointApprovalService(approveCtx).ApproveAsync(requestId, new ApproveRequest(customerB));

        Assert.Equal("Approved", response.Status);
        Assert.NotNull(response.Grant);
    }

    // --- Task file test: "Approving twice as the same holder is rejected (clean error, not just relying
    // on the DB constraint to surface a raw error)." --------------------------------------------------

    [Fact]
    public async Task ApproveAsync_Throws_WhenTheSameHolderApprovesTwice()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var customerC = await CreateCustomerAsync("Holder C");
        await AttachMandateAsync(customerA, "ACC-DUPAPPROVE", "Debit");
        await AttachMandateAsync(customerB, "ACC-DUPAPPROVE", "Debit");
        await AttachMandateAsync(customerC, "ACC-DUPAPPROVE", "Debit");
        await SetRequiredApprovalsAsync("ACC-DUPAPPROVE", 3, customerA, customerB, customerC);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-DUPAPPROVE"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        // Requester A approving again (the same CustomerId that implicitly approved at creation).
        await using var ctx2 = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<DuplicateApprovalException>(
            () => CreateJointApprovalService(ctx2).ApproveAsync(requestId, new ApproveRequest(customerA)));

        Assert.Contains(customerA, ex.Message);

        // Scoped to THIS request specifically - SetRequiredApprovalsAsync's own ThresholdChange request
        // (setup, above) also recorded an ApprovalRecord for customerA, on a different request.
        await using var verifyCtx = fixture.CreateContext();
        var approvals = await verifyCtx.ApprovalRecords
            .Include(a => a.PendingApprovalRequest)
            .Where(a => a.CustomerId == customerA && a.PendingApprovalRequest.RequestId == requestId)
            .ToListAsync();
        Assert.Single(approvals); // the duplicate attempt did not create a second row
    }

    // --- Task file test: "A request past its ExpiresAt: a GET after expiry shows Expired; an
    // .../approve attempt after expiry is rejected and the request is (or already was) marked Expired."

    [Fact]
    public async Task ExpiredRequest_GetShowsExpired_AndApproveIsRejected()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-EXPIRE", "Debit");
        await AttachMandateAsync(customerB, "ACC-EXPIRE", "Debit");
        await SetRequiredApprovalsAsync("ACC-EXPIRE", 2, customerA, customerB);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-EXPIRE"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        // Force the request into the past without waiting a real hour.
        await using (var ctx = fixture.CreateContext())
        {
            var pending = await ctx.PendingApprovalRequests.SingleAsync(r => r.RequestId == requestId);
            pending.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await ctx.SaveChangesAsync();
        }

        await using (var getCtx = fixture.CreateContext())
        {
            var response = await CreateJointApprovalService(getCtx).GetPendingRequestAsync(requestId);
            Assert.Equal("Expired", response!.Status);
        }

        // The GET above already persisted the transition - confirm it is durable, then confirm approve
        // is rejected (whether or not GET had been called first).
        await using var verifyCtx = fixture.CreateContext();
        var stored = await verifyCtx.PendingApprovalRequests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);
        Assert.Equal(PendingApprovalRequestStatus.Expired, stored.Status);

        await using var approveCtx = fixture.CreateContext();
        await Assert.ThrowsAsync<PendingApprovalRequestNotPendingException>(
            () => CreateJointApprovalService(approveCtx).ApproveAsync(requestId, new ApproveRequest(customerB)));
    }

    [Fact]
    public async Task ExpiredRequest_ApproveAttempt_MarksItExpired_EvenIfNoGetHappenedFirst()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-EXPIRE2", "Debit");
        await AttachMandateAsync(customerB, "ACC-EXPIRE2", "Debit");
        await SetRequiredApprovalsAsync("ACC-EXPIRE2", 2, customerA, customerB);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-EXPIRE2"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        await using (var ctx = fixture.CreateContext())
        {
            var pending = await ctx.PendingApprovalRequests.SingleAsync(r => r.RequestId == requestId);
            pending.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await ctx.SaveChangesAsync();
        }

        await using var approveCtx = fixture.CreateContext();
        await Assert.ThrowsAsync<PendingApprovalRequestNotPendingException>(
            () => CreateJointApprovalService(approveCtx).ApproveAsync(requestId, new ApproveRequest(customerB)));

        await using var verifyCtx = fixture.CreateContext();
        var stored = await verifyCtx.PendingApprovalRequests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);
        Assert.Equal(PendingApprovalRequestStatus.Expired, stored.Status);
    }

    // --- Task file test: "Revoking a holder's mandate while their approval is outstanding on a Pending
    // request voids that approval; a subsequent attempt to complete the threshold using the old
    // (now-voided) approval count fails - the request stays Pending." --------------------------------

    [Fact]
    public async Task RevokingAHoldersMandate_VoidsTheirOutstandingApproval_AndTheRequestStaysPending()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var customerC = await CreateCustomerAsync("Holder C");
        await AttachMandateAsync(customerA, "ACC-REVOKEVOID", "Debit");
        var mandateB = await AttachMandateAsync(customerB, "ACC-REVOKEVOID", "Debit");
        await AttachMandateAsync(customerC, "ACC-REVOKEVOID", "Debit");
        await SetRequiredApprovalsAsync("ACC-REVOKEVOID", 3, customerA, customerB, customerC);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-REVOKEVOID"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        // B approves - 2 of 3 so far (A's implicit + B's).
        await using (var ctx = fixture.CreateContext())
        {
            await CreateJointApprovalService(ctx).ApproveAsync(requestId, new ApproveRequest(customerB));
        }

        // B's mandate is revoked WHILE their approval is outstanding - the eager half of decision #24.
        await RevokeMandateAsync(mandateB, "no longer trusted");

        await using (var verifyCtx = fixture.CreateContext())
        {
            var bApproval = await verifyCtx.ApprovalRecords
                .Include(a => a.PendingApprovalRequest)
                .SingleAsync(a => a.CustomerId == customerB && a.PendingApprovalRequest.RequestId == requestId);
            Assert.NotNull(bApproval.VoidedAt);
        }

        // C approves next - without the live re-check even running, B's voided approval must not count:
        // only A (requester) + C are valid = 2, still short of the 3 required.
        await using var approveCtx = fixture.CreateContext();
        var response = await CreateJointApprovalService(approveCtx).ApproveAsync(requestId, new ApproveRequest(customerC));

        Assert.Equal("Pending", response.Status);
        Assert.Equal(2, response.ApprovalCount); // A + C; B's voided approval does not count
        Assert.Null(response.Grant);
    }

    // --- Task file test: "The live re-check at threshold-completion: construct a case where an approval
    // was valid when given but the holder is revoked before the last approval arrives - confirm the
    // grant is NOT issued and the request stays Pending." --------------------------------------------
    //
    // This isolates the SECOND half of decision #24 from the first: the holder's mandate is revoked by
    // directly updating the row (bypassing MandateService.RevokeMandateAsync's own eager void entirely),
    // so the ApprovalRecord is NOT pre-voided when the last approval arrives - only ApproveAsync's own
    // live re-check (which queries Mandates directly, not ApprovalRecord.VoidedAt) can catch it.

    [Fact]
    public async Task LiveRecheckAtThresholdCompletion_CatchesAHolderRevokedWithoutGoingThroughTheEagerVoidPath()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-LIVERECHECK", "Debit");
        await AttachMandateAsync(customerB, "ACC-LIVERECHECK", "Debit");
        await SetRequiredApprovalsAsync("ACC-LIVERECHECK", 2, customerA, customerB);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-LIVERECHECK"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        // Directly flip A's (the requester's own, already-counted) mandate to Revoked - bypassing
        // MandateService.RevokeMandateAsync entirely, so the eager-void mechanism never runs and A's
        // ApprovalRecord.VoidedAt stays null. Only a live re-check against Mandates itself can catch this.
        await using (var ctx = fixture.CreateContext())
        {
            var mandate = await ctx.Mandates.Include(m => m.Customer)
                .SingleAsync(m => m.Customer.CustomerId == customerA && m.LedgerAccountId == "ACC-LIVERECHECK");
            mandate.Status = MandateStatus.Revoked;
            mandate.RevokedAt = DateTimeOffset.UtcNow;
            mandate.RevokedBy = "test-direct-update";
            mandate.RevocationReason = "simulated out-of-band revoke, bypassing eager void";
            await ctx.SaveChangesAsync();
        }

        // Sanity check: A's ApprovalRecord is still NOT voided (the eager path never ran).
        await using (var verifyCtx = fixture.CreateContext())
        {
            var aApproval = await verifyCtx.ApprovalRecords
                .Include(a => a.PendingApprovalRequest)
                .SingleAsync(a => a.CustomerId == customerA && a.PendingApprovalRequest.RequestId == requestId);
            Assert.Null(aApproval.VoidedAt);
        }

        // B submits the last (2nd of 2) approval - the live re-check must independently discover A's
        // mandate is no longer Active and refuse to complete the request.
        await using var approveCtx = fixture.CreateContext();
        var response = await CreateJointApprovalService(approveCtx).ApproveAsync(requestId, new ApproveRequest(customerB));

        Assert.Equal("Pending", response.Status);
        Assert.Null(response.Grant);
        Assert.Null(response.DecisionId);

        // The live re-check voids the now-stale approval it found, same as decision #24 requires.
        await using var finalCtx = fixture.CreateContext();
        var aApprovalAfter = await finalCtx.ApprovalRecords
            .Include(a => a.PendingApprovalRequest)
            .SingleAsync(a => a.CustomerId == customerA && a.PendingApprovalRequest.RequestId == requestId);
        Assert.NotNull(aApprovalAfter.VoidedAt);
    }

    // --- Task file test: "Threshold reconfiguration: a 2-of-3 account's change request needs all 3
    // holders, not just 2; completing it updates the policy; a subsequent debit respects the new
    // threshold." -------------------------------------------------------------------------------------

    [Fact]
    public async Task ThresholdChange_OnAThreeHolderAccount_NeedsAllThreeHolders_NotJustTwo()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var customerC = await CreateCustomerAsync("Holder C");
        await AttachMandateAsync(customerA, "ACC-ALLTHREE", "Debit");
        await AttachMandateAsync(customerB, "ACC-ALLTHREE", "Debit");
        await AttachMandateAsync(customerC, "ACC-ALLTHREE", "Debit");

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var response = await CreateJointApprovalService(ctx).RequestThresholdChangeAsync(
                "ACC-ALLTHREE", new ChangeApprovalPolicyRequest(customerA, 2));
            requestId = response.RequestId;
            Assert.Equal(3, response.RequiredApprovals); // forced to active-holder count, not the requested value
            Assert.Equal(2, response.RequestedRequiredApprovals);
        }

        // Only holder B approves in addition to the requester - 2 of 3, not enough (decision #22 needs
        // every holder, not merely today's threshold's worth).
        await using (var ctx = fixture.CreateContext())
        {
            var response = await CreateJointApprovalService(ctx).ApproveAsync(requestId, new ApproveRequest(customerB));
            Assert.Equal("Pending", response.Status);
        }

        await using (var verifyCtx = fixture.CreateContext())
        {
            var policy = await verifyCtx.MandateApprovalPolicies.AsNoTracking().SingleAsync(p => p.LedgerAccountId == "ACC-ALLTHREE");
            Assert.Equal(1, policy.RequiredApprovals); // unchanged - not yet unanimous
        }

        // Holder C (the last holder) approves - now unanimous, so it completes and applies the change.
        await using var finalCtx = fixture.CreateContext();
        var finalResponse = await CreateJointApprovalService(finalCtx).ApproveAsync(requestId, new ApproveRequest(customerC));

        Assert.Equal("Approved", finalResponse.Status);
        Assert.Null(finalResponse.Grant); // no money movement for a ThresholdChange (task item 4)

        await using var policyCtx = fixture.CreateContext();
        var updatedPolicy = await policyCtx.MandateApprovalPolicies.AsNoTracking().SingleAsync(p => p.LedgerAccountId == "ACC-ALLTHREE");
        Assert.Equal(2, updatedPolicy.RequiredApprovals);
        Assert.Equal(requestId, updatedPolicy.LastChangedBy);

        // A subsequent debit now respects the new 2-of-3 threshold rather than granting on one approval.
        await using var debitCtx = fixture.CreateContext();
        var debitResult = await CreateJointApprovalService(debitCtx).RequestDebitAsync(DebitRequest(customerA, "ACC-ALLTHREE"));
        var pending = Assert.IsType<DebitRequestResult.Pending>(debitResult).Response;
        Assert.Equal(2, pending.RequiredApprovals);
    }

    [Fact]
    public async Task RequestThresholdChangeAsync_Throws_WhenRequiredApprovalsExceedsActiveHolderCount()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-TOOMANY", "Debit");
        await AttachMandateAsync(customerB, "ACC-TOOMANY", "Debit");

        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<InvalidApprovalThresholdException>(() =>
            CreateJointApprovalService(ctx).RequestThresholdChangeAsync(
                "ACC-TOOMANY", new ChangeApprovalPolicyRequest(customerA, 3)));
    }

    [Fact]
    public async Task RequestThresholdChangeAsync_Throws_WhenRequesterIsNotACurrentHolder()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var outsider = await CreateCustomerAsync("Outsider");
        await AttachMandateAsync(customerA, "ACC-NOTAHOLDER", "Debit");

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<JointApprovalDeniedException>(() =>
            CreateJointApprovalService(ctx).RequestThresholdChangeAsync(
                "ACC-NOTAHOLDER", new ChangeApprovalPolicyRequest(outsider, 1)));

        Assert.Equal(AuthorizationDenialReasons.NoActiveMandate, ex.ReasonCode);
    }

    [Fact]
    public async Task RequestThresholdChangeAsync_Throws_WhenNoApprovalPolicyExistsForTheAccount()
    {
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<ApprovalPolicyNotFoundException>(() =>
            CreateJointApprovalService(ctx).RequestThresholdChangeAsync(
                "ACC-NOPOLICY", new ChangeApprovalPolicyRequest("CUS-X", 1)));
    }

    // --- GetApprovalPolicyAsync ------------------------------------------------------------------------

    [Fact]
    public async Task GetApprovalPolicyAsync_ReturnsCurrentThresholdAndActiveHolderCount()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-POLICYVIEW", "View");
        var mandateB = await AttachMandateAsync(customerB, "ACC-POLICYVIEW", "View");
        await RevokeMandateAsync(mandateB, "left the account");

        await using var ctx = fixture.CreateContext();
        var policy = await CreateJointApprovalService(ctx).GetApprovalPolicyAsync("ACC-POLICYVIEW");

        Assert.NotNull(policy);
        Assert.Equal(1, policy!.RequiredApprovals);
        Assert.Equal(1, policy.ActiveHolderCount); // B was revoked, only A is active
    }

    [Fact]
    public async Task GetApprovalPolicyAsync_ReturnsNull_WhenNoPolicyExistsForTheAccount()
    {
        await using var ctx = fixture.CreateContext();
        var policy = await CreateJointApprovalService(ctx).GetApprovalPolicyAsync("ACC-NEVERSEEN");

        Assert.Null(policy);
    }

    // --- Approve-endpoint validation (3.8.1's list) --------------------------------------------------

    [Fact]
    public async Task ApproveAsync_Throws_WhenTheApproverHasNoActiveMandateOnTheAccount()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var outsider = await CreateCustomerAsync("Outsider");
        await AttachMandateAsync(customerA, "ACC-APPROVERNOMANDATE", "Debit");
        await AttachMandateAsync(customerB, "ACC-APPROVERNOMANDATE", "Debit");
        await SetRequiredApprovalsAsync("ACC-APPROVERNOMANDATE", 2, customerA, customerB);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-APPROVERNOMANDATE"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        await using var ctx2 = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<JointApprovalDeniedException>(
            () => CreateJointApprovalService(ctx2).ApproveAsync(requestId, new ApproveRequest(outsider)));

        Assert.Equal(AuthorizationDenialReasons.NoActiveMandate, ex.ReasonCode);
    }

    [Fact]
    public async Task ApproveAsync_Throws_WhenTheApproverSMandateDoesNotPermitTheOperation()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-NOPERMISSION", "Debit");
        await AttachMandateAsync(customerB, "ACC-NOPERMISSION", "View"); // no Debit permission
        await SetRequiredApprovalsAsync("ACC-NOPERMISSION", 2, customerA, customerB);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-NOPERMISSION"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        await using var ctx2 = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<JointApprovalDeniedException>(
            () => CreateJointApprovalService(ctx2).ApproveAsync(requestId, new ApproveRequest(customerB)));

        Assert.Equal(AuthorizationDenialReasons.OperationNotPermittedByMandate, ex.ReasonCode);
    }

    [Fact]
    public async Task ApproveAsync_Throws_ForAnUnknownRequestId()
    {
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<PendingApprovalRequestNotFoundException>(
            () => CreateJointApprovalService(ctx).ApproveAsync("PAR-DOESNOTEXIST", new ApproveRequest("CUS-X")));
    }

    [Fact]
    public async Task GetPendingRequestAsync_ReturnsNull_ForAnUnknownRequestId()
    {
        await using var ctx = fixture.CreateContext();
        var response = await CreateJointApprovalService(ctx).GetPendingRequestAsync("PAR-DOESNOTEXIST");

        Assert.Null(response);
    }

    // DESIGN.md Section 6.2/decision #13: the grant is only ever returned on the exact approve call that
    // issues it - a later GET of the same (now Approved) request must never re-serve it.
    [Fact]
    public async Task GetPendingRequestAsync_NeverReturnsTheGrant_EvenAfterTheRequestIsApproved()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        await AttachMandateAsync(customerA, "ACC-NOREGRANT", "Debit");
        await AttachMandateAsync(customerB, "ACC-NOREGRANT", "Debit");
        await SetRequiredApprovalsAsync("ACC-NOREGRANT", 2, customerA, customerB);

        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var result = await CreateJointApprovalService(ctx).RequestDebitAsync(DebitRequest(customerA, "ACC-NOREGRANT"));
            requestId = Assert.IsType<DebitRequestResult.Pending>(result).Response.RequestId;
        }

        await using (var ctx = fixture.CreateContext())
        {
            var approveResponse = await CreateJointApprovalService(ctx).ApproveAsync(requestId, new ApproveRequest(customerB));
            Assert.NotNull(approveResponse.Grant); // present exactly once, on this call
        }

        await using var getCtx = fixture.CreateContext();
        var getResponse = await CreateJointApprovalService(getCtx).GetPendingRequestAsync(requestId);

        Assert.Equal("Approved", getResponse!.Status);
        Assert.NotNull(getResponse.DecisionId);
        Assert.Null(getResponse.Grant);
    }
}
