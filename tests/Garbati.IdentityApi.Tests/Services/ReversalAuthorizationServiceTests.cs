using System.Globalization;
using System.Security.Cryptography;
using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Entities;
using Garbati.IdentityApi.Exceptions;
using Garbati.IdentityApi.Services;
using Garbati.IdentityApi.Signing;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Garbati.IdentityApi.Tests.Services;

// DESIGN.md Section 8 (decisions #30-#35), garbati-platform T-020. This is the service-level counterpart
// to AuthorizationServiceTests.cs (T-017): same "sign a real grant, verify it the way a future verifier
// eventually would" style, but proving the OPPOSITE thing on purpose - that a Reversal request bypasses
// every customer/KYC/mandate check that AuthorizationServiceTests.cs proves DOES gate an ordinary Debit.
[Collection("Postgres")]
public class ReversalAuthorizationServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IGrantSigner CreateGrantSigner() => new GrantSigner(Options.Create(new SigningKeyOptions
    {
        ActiveKid = TestSigningKey.Kid,
        Keys = [new SigningKeyEntry { Kid = TestSigningKey.Kid, PrivateKeyPem = TestSigningKey.PrivateKeyPem }],
    }));

    private static ReversalAuthorizationService CreateService(Garbati.IdentityApi.Data.IdentityDbContext ctx) =>
        new(ctx, TestAudit.For(ctx), CreateGrantSigner());

    private static AuthorizationRequest ReversalRequest(
        string requestedBy = "OPS-ACTOR", string reference = "TXN-ORIGINAL-1", string reasonCode = "FRAD", decimal amount = 5000m) =>
        new(requestedBy, "Reversal", reference, null, amount, "NGN", $"idem-{Guid.NewGuid()}", reasonCode);

    private static TokenValidationParameters PublicKeyValidationParameters()
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(TestSigningKey.PublicKeyPem);
        return new TokenValidationParameters
        {
            ValidIssuer = GrantSigner.Issuer,
            ValidAudience = GrantSigner.Audience,
            IssuerSigningKey = new ECDsaSecurityKey(ecdsa),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ClockSkew = TimeSpan.FromSeconds(5),
        };
    }

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

    private async Task AttachMandateAsync(string customerId, string ledgerAccountId, params string[] operations)
    {
        await using var ctx = fixture.CreateContext();
        await new MandateService(ctx, TestAudit.For(ctx), TestCaller.Default)
            .AttachMandateAsync(customerId, new AttachMandateRequest(ledgerAccountId, operations.ToList()));
    }

    // Raises an account's threshold using the real reconfiguration flow (mirrors JointApprovalServiceTests).
    private async Task SetRequiredApprovalsAsync(string ledgerAccountId, int requiredApprovals, params string[] holderCustomerIds)
    {
        string requestId;
        await using (var ctx = fixture.CreateContext())
        {
            var jointService = new JointApprovalService(ctx, TestAudit.For(ctx), new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner()));
            var response = await jointService.RequestThresholdChangeAsync(
                ledgerAccountId, new ChangeApprovalPolicyRequest(holderCustomerIds[0], requiredApprovals));
            requestId = response.RequestId;
        }

        foreach (var holder in holderCustomerIds.Skip(1))
        {
            await using var ctx = fixture.CreateContext();
            var jointService = new JointApprovalService(ctx, TestAudit.For(ctx), new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner()));
            await jointService.ApproveAsync(requestId, new ApproveRequest(holder));
        }
    }

    // --- The single most important test in this task: the mandate bypass is real. ---------------------

    [Fact]
    public async Task RequestReversalAsync_Succeeds_ForAnAccountWithNoMandatesAtAll()
    {
        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest(reference: "ACC-NO-MANDATE"));

        Assert.True(response.Decision.Approved);
        Assert.NotEmpty(response.Grant);
    }

    [Fact]
    public async Task RequestReversalAsync_Succeeds_ForASingleHolderAccount()
    {
        var customerId = await CreateCustomerAsync("Solo Holder");
        await AttachMandateAsync(customerId, "ACC-SOLO-REV", "Debit");

        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest(reference: "ACC-SOLO-REV"));

        Assert.True(response.Decision.Approved);
        Assert.NotEmpty(response.Grant);
    }

    [Fact]
    public async Task RequestReversalAsync_Succeeds_ForAJointAccount_WithNoApprovalsCollectedAtAll()
    {
        var customerA = await CreateCustomerAsync("Holder A");
        var customerB = await CreateCustomerAsync("Holder B");
        var customerC = await CreateCustomerAsync("Holder C");
        await AttachMandateAsync(customerA, "ACC-2OF3-REV", "Debit");
        await AttachMandateAsync(customerB, "ACC-2OF3-REV", "Debit");
        await AttachMandateAsync(customerC, "ACC-2OF3-REV", "Debit");
        await SetRequiredApprovalsAsync("ACC-2OF3-REV", 2, customerA, customerB, customerC);

        // Proves the bypass is real, not accidentally still gated: a joint account's own debit would be
        // Pending at this point (no approvals collected beyond the would-be requester's), but a reversal
        // against the SAME account succeeds immediately - decision #31's "ops override" in action.
        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest(reference: "ACC-2OF3-REV"));

        Assert.True(response.Decision.Approved);
        Assert.NotEmpty(response.Grant);
    }

    [Fact]
    public async Task RequestReversalAsync_Succeeds_EvenForASuspendedOrNonexistentCustomer()
    {
        // Decision #31: "no customer status check, no KYC check" - RequestedBy is never looked up as a
        // Customer at all for a Reversal, unlike every other operation this endpoint accepts.
        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest(requestedBy: "CUS-DOES-NOT-EXIST"));

        Assert.True(response.Decision.Approved);
        Assert.Equal("CUS-DOES-NOT-EXIST", response.Decision.RequestedBy);
    }

    // --- Grant content and tampering ---------------------------------------------------------------

    [Fact]
    public async Task RequestReversalAsync_IssuesAGrant_WithOperationReversalAndTheReasonCodeAsClaims()
    {
        await using var ctx = fixture.CreateContext();
        var request = ReversalRequest(reference: "TXN-CLAIMS-TEST", reasonCode: "DUPL", amount: 1234.56m);
        var response = await CreateService(ctx).RequestReversalAsync(request);

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(response.Grant, PublicKeyValidationParameters());
        Assert.True(result.IsValid, result.Exception?.ToString());

        var jwt = handler.ReadJsonWebToken(response.Grant);
        Assert.Equal("Reversal", jwt.GetClaim("operation").Value);
        Assert.Equal("DUPL", jwt.GetClaim("reasonCode").Value);
        Assert.Equal("TXN-CLAIMS-TEST", jwt.GetClaim("debitAccountId").Value);
        Assert.Equal(request.Amount.ToString("G29", CultureInfo.InvariantCulture), jwt.GetClaim("amount").Value);
        Assert.Equal(request.RequestedBy, jwt.GetClaim("sub").Value);

        // Decision #31: no mandate backs a reversal - the claim must be genuinely absent, not an empty
        // string standing in for "none" (that would be a value a tampering attacker could forge/believe).
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "mandateId");

        Assert.Equal(response.Decision.DecisionId, jwt.GetClaim("jti").Value);
        var exp = long.Parse(jwt.GetClaim("exp").Value);
        var iat = long.Parse(jwt.GetClaim("iat").Value);
        Assert.Equal(30, exp - iat);
    }

    [Fact]
    public async Task RequestReversalAsync_PersistsTheDecision_WithTheReasonCode()
    {
        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest(reasonCode: "UNAUTH"));

        await using var verifyCtx = fixture.CreateContext();
        var decision = await verifyCtx.AuthorizationDecisions.AsNoTracking()
            .SingleAsync(d => d.DecisionId == response.Decision.DecisionId);

        Assert.Equal("Reversal", decision.Operation);
        Assert.Equal("UNAUTH", decision.ReasonCode);
        Assert.True(decision.Approved);
        Assert.Null(decision.MandateId);
    }

    [Fact]
    public async Task RequestReversalAsync_NeverPersistsOrAuditsTheGrantTokenItself()
    {
        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest());

        await using var verifyCtx = fixture.CreateContext();
        var audits = await verifyCtx.AuditEvents.ToListAsync();
        Assert.All(audits, a =>
        {
            Assert.DoesNotContain(response.Grant, a.Details ?? "");
            Assert.DoesNotContain(response.Grant, a.Action);
            Assert.DoesNotContain(response.Grant, a.Status ?? "");
        });
    }

    // Appendix A-style check (applied to the new operation): tampering with either the operation claim or
    // the reasonCode claim must be caught by signature verification, the same simple in-test ES256 check
    // T-017/T-018 already use (no real LedgerApi verifier exists - decision #27/D-6).
    [Fact]
    public async Task TamperedReversalGrant_FailsSignatureVerification()
    {
        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest());

        var parts = response.Grant.Split('.');
        Assert.Equal(3, parts.Length);

        var payloadChars = parts[1].ToCharArray();
        var flipIndex = payloadChars.Length / 2;
        payloadChars[flipIndex] = payloadChars[flipIndex] == 'A' ? 'B' : 'A';
        var tampered = string.Join('.', parts[0], new string(payloadChars), parts[2]);

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(tampered, PublicKeyValidationParameters());

        Assert.False(result.IsValid);
    }

    // --- Outcome recording (8.6) --------------------------------------------------------------------

    private async Task<string> CreateApprovedReversalDecisionAsync()
    {
        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RequestReversalAsync(ReversalRequest());
        return response.Decision.DecisionId;
    }

    [Fact]
    public async Task RecordOutcomeAsync_RecordsACompletedOutcome()
    {
        var decisionId = await CreateApprovedReversalDecisionAsync();

        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RecordOutcomeAsync(
            decisionId, new RecordReversalOutcomeRequest("Completed", "ops-user-1", "Recovered in full"));

        Assert.Equal(decisionId, response.AuthorizationDecisionId);
        Assert.Equal("Completed", response.Outcome);
        Assert.Equal("ops-user-1", response.RecordedBy);
        Assert.Equal("Recovered in full", response.Notes);
    }

    [Fact]
    public async Task RecordOutcomeAsync_RecordsAWriteOffOutcome_OnADifferentDecision()
    {
        var decisionId = await CreateApprovedReversalDecisionAsync();

        await using var ctx = fixture.CreateContext();
        var response = await CreateService(ctx).RecordOutcomeAsync(
            decisionId, new RecordReversalOutcomeRequest("WriteOff", "ops-user-2", null));

        Assert.Equal("WriteOff", response.Outcome);
        Assert.Null(response.Notes);
    }

    [Fact]
    public async Task RecordOutcomeAsync_Rejects_ASecondOutcomeOnTheSameDecision()
    {
        var decisionId = await CreateApprovedReversalDecisionAsync();

        await using (var ctx = fixture.CreateContext())
        {
            await CreateService(ctx).RecordOutcomeAsync(
                decisionId, new RecordReversalOutcomeRequest("Completed", "ops-user-1", null));
        }

        await using var secondCtx = fixture.CreateContext();
        await Assert.ThrowsAsync<DuplicateReversalOutcomeException>(() => CreateService(secondCtx).RecordOutcomeAsync(
            decisionId, new RecordReversalOutcomeRequest("WriteOff", "ops-user-2", null)));
    }

    [Fact]
    public async Task RecordOutcomeAsync_Rejects_WhenTheDecisionWasDenied()
    {
        // A denied decision only ever exists for non-Reversal operations today (RequestReversalAsync has
        // no denial path - see its own comment) - constructed directly to prove the eligibility check
        // itself, independent of how a denied decision could come to exist.
        await using var ctx = fixture.CreateContext();
        ctx.AuthorizationDecisions.Add(new AuthorizationDecision
        {
            DecisionId = "DEC-DENIED-REVERSAL",
            RequestedBy = "OPS-ACTOR",
            Operation = "Reversal",
            DebitAccountId = "TXN-1",
            Amount = "100",
            Currency = "NGN",
            IdempotencyKey = "idem-denied",
            ReasonCode = "FRAD",
            Approved = false,
            DenialReason = "SomeReason",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await ctx.SaveChangesAsync();

        await using var verifyCtx = fixture.CreateContext();
        await Assert.ThrowsAsync<ReversalOutcomeNotEligibleException>(() => CreateService(verifyCtx).RecordOutcomeAsync(
            "DEC-DENIED-REVERSAL", new RecordReversalOutcomeRequest("Completed", "ops-user-1", null)));
    }

    [Fact]
    public async Task RecordOutcomeAsync_Rejects_WhenTheDecisionsOperationWasNotReversal()
    {
        var customerId = await CreateCustomerAsync("Ordinary Debit Holder");
        await AttachMandateAsync(customerId, "ACC-NOT-REVERSAL", "Debit");

        await using var ctx = fixture.CreateContext();
        var ordinaryResponse = await new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner())
            .RequestAuthorizationAsync(new AuthorizationRequest(
                customerId, "Debit", "ACC-NOT-REVERSAL", "ACC-DEST", 100m, "NGN", $"idem-{Guid.NewGuid()}"));

        await using var verifyCtx = fixture.CreateContext();
        await Assert.ThrowsAsync<ReversalOutcomeNotEligibleException>(() => CreateService(verifyCtx).RecordOutcomeAsync(
            ordinaryResponse.Decision.DecisionId, new RecordReversalOutcomeRequest("Completed", "ops-user-1", null)));
    }

    [Fact]
    public async Task RecordOutcomeAsync_Rejects_WhenTheDecisionDoesNotExist()
    {
        await using var ctx = fixture.CreateContext();
        await Assert.ThrowsAsync<AuthorizationDecisionNotFoundException>(() => CreateService(ctx).RecordOutcomeAsync(
            "DEC-DOES-NOT-EXIST", new RecordReversalOutcomeRequest("Completed", "ops-user-1", null)));
    }
}
