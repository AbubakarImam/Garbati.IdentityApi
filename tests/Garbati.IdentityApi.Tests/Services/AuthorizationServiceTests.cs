using System.Globalization;
using System.Security.Cryptography;
using Garbati.IdentityApi.Auditing;
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

// DESIGN.md Section 3 (3.1-3.7 only) and Appendix A's security-acceptance items that apply to this
// task's scope. No LedgerApi/verifier exists yet (decision #27/D-6), so the "would-be verifier" checks
// here are the simple in-test check the task file explicitly allows: verify the grant with the matching
// public key directly, the same way a real verifier eventually would.
[Collection("Postgres")]
public class AuthorizationServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static IGrantSigner CreateGrantSigner() => new GrantSigner(Options.Create(new SigningKeyOptions
    {
        ActiveKid = TestSigningKey.Kid,
        Keys = [new SigningKeyEntry { Kid = TestSigningKey.Kid, PrivateKeyPem = TestSigningKey.PrivateKeyPem }],
    }));

    private async Task<string> CreateCustomerAsync(
        string? kycTier = "Tier2", CustomerStatus status = CustomerStatus.Active, string fullName = "Ada Lovelace")
    {
        await using var ctx = fixture.CreateContext();
        var customerService = new CustomerService(ctx, TestAudit.For(ctx), TestCaller.Default);
        var response = await customerService.CreateCustomerAsync(
            new CreateCustomerRequest(fullName, "NG", null, null, null, Guid.NewGuid().ToString()));

        if (kycTier is not null)
        {
            await customerService.RecordKycDecisionAsync(
                response.CustomerId,
                new KycDecisionRequest("Verified", kycTier, "BVN", "test-provider", null, "OK", null));
        }

        if (status == CustomerStatus.Suspended)
        {
            await customerService.SuspendCustomerAsync(response.CustomerId, new CustomerStatusChangeRequest("test suspension"));
        }
        else if (status == CustomerStatus.Closed)
        {
            await customerService.CloseCustomerAsync(response.CustomerId, new CustomerStatusChangeRequest("test closure"));
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

    private static AuthorizationRequest Request(string customerId, string debitAccountId = "ACC-1", string operation = "Debit", decimal amount = 5000m) =>
        new(customerId, operation, debitAccountId, "ACC-2", amount, "NGN", $"idem-{Guid.NewGuid()}");

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

    [Fact]
    public async Task RequestAuthorizationAsync_IssuesAValidGrant_ForAnEligibleCustomerWithAnActiveMandate()
    {
        var customerId = await CreateCustomerAsync();
        var mandateId = await AttachMandateAsync(customerId, "ACC-1", "Debit");

        await using var ctx = fixture.CreateContext();
        var request = Request(customerId, amount: 5000.5m);
        var response = await new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner())
            .RequestAuthorizationAsync(request);

        Assert.True(response.Decision.Approved);
        Assert.Null(response.Decision.DenialReason);
        Assert.Equal(mandateId, response.Decision.MandateId);
        Assert.NotEmpty(response.Grant);

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(response.Grant, PublicKeyValidationParameters());
        Assert.True(result.IsValid, result.Exception?.ToString());

        var jwt = handler.ReadJsonWebToken(response.Grant);
        Assert.Equal(response.Decision.DecisionId, jwt.GetClaim("jti").Value);
        Assert.Equal(customerId, jwt.GetClaim("sub").Value);
        Assert.Equal("transaction-authorization", jwt.GetClaim("typ").Value);
        Assert.Equal(TestSigningKey.Kid, jwt.GetClaim("kid").Value);
        Assert.Equal("Debit", jwt.GetClaim("operation").Value);
        Assert.Equal("ACC-1", jwt.GetClaim("debitAccountId").Value);
        Assert.Equal("ACC-2", jwt.GetClaim("creditAccountId").Value);
        Assert.Equal(request.Amount.ToString("G29", CultureInfo.InvariantCulture), jwt.GetClaim("amount").Value);
        Assert.Equal("NGN", jwt.GetClaim("currency").Value);
        Assert.Equal(request.IdempotencyKey, jwt.GetClaim("idempotencyKey").Value);
        Assert.Equal(mandateId, jwt.GetClaim("mandateId").Value);
        Assert.Equal(TestSigningKey.Kid, jwt.Kid); // JOSE header kid, not just the body claim.

        var exp = long.Parse(jwt.GetClaim("exp").Value);
        var iat = long.Parse(jwt.GetClaim("iat").Value);
        Assert.Equal(30, exp - iat);
    }

    [Fact]
    public async Task RequestAuthorizationAsync_PersistsTheDecision_RetrievableByGetDecisionAsync()
    {
        var customerId = await CreateCustomerAsync();
        await AttachMandateAsync(customerId, "ACC-1", "Debit");

        await using var ctx = fixture.CreateContext();
        var service = new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner());
        var response = await service.RequestAuthorizationAsync(Request(customerId));

        await using var verifyCtx = fixture.CreateContext();
        var decision = await new AuthorizationService(verifyCtx, TestAudit.For(verifyCtx), CreateGrantSigner())
            .GetDecisionAsync(response.Decision.DecisionId);

        Assert.NotNull(decision);
        Assert.True(decision!.Approved);
        Assert.Equal(customerId, decision.RequestedBy);
    }

    [Fact]
    public async Task RequestAuthorizationAsync_NeverPersistsOrAuditsTheGrantTokenItself()
    {
        var customerId = await CreateCustomerAsync();
        await AttachMandateAsync(customerId, "ACC-1", "Debit");

        await using var ctx = fixture.CreateContext();
        var response = await new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner())
            .RequestAuthorizationAsync(Request(customerId));

        await using var verifyCtx = fixture.CreateContext();
        var audits = await verifyCtx.AuditEvents.ToListAsync();
        Assert.All(audits, a =>
        {
            Assert.DoesNotContain(response.Grant, a.Details ?? "");
            Assert.DoesNotContain(response.Grant, a.Action);
            Assert.DoesNotContain(response.Grant, a.Status ?? "");
        });

        var decisions = await verifyCtx.AuthorizationDecisions.ToListAsync();
        Assert.All(decisions, d => Assert.DoesNotContain("eyJ", d.DenialReason ?? "")); // not a JWT fragment anywhere persisted
    }

    [Fact]
    public async Task RequestAuthorizationAsync_Denies_WhenTheCustomerDoesNotExist()
    {
        await using var ctx = fixture.CreateContext();
        var service = new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner());

        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => service.RequestAuthorizationAsync(Request("CUS-DOESNOTEXIST")));

        Assert.Equal(AuthorizationDenialReasons.CustomerNotFound, ex.ReasonCode);

        await using var verifyCtx = fixture.CreateContext();
        var decision = await verifyCtx.AuthorizationDecisions.SingleAsync(d => d.DecisionId == ex.DecisionId);
        Assert.False(decision.Approved);
    }

    [Theory]
    [InlineData(CustomerStatus.Suspended)]
    [InlineData(CustomerStatus.Closed)]
    public async Task RequestAuthorizationAsync_Denies_ForASuspendedOrClosedCustomer(CustomerStatus status)
    {
        var customerId = await CreateCustomerAsync(status: status);

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner()).RequestAuthorizationAsync(Request(customerId)));

        Assert.Equal(AuthorizationDenialReasons.CustomerNotActive, ex.ReasonCode);
    }

    [Fact]
    public async Task RequestAuthorizationAsync_Denies_WhenNoMandateExistsOnTheDebitAccount()
    {
        var customerId = await CreateCustomerAsync();

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner()).RequestAuthorizationAsync(Request(customerId)));

        Assert.Equal(AuthorizationDenialReasons.NoActiveMandate, ex.ReasonCode);
    }

    [Fact]
    public async Task RequestAuthorizationAsync_Denies_WhenTheMandateDoesNotPermitTheRequestedOperation()
    {
        var customerId = await CreateCustomerAsync();
        await AttachMandateAsync(customerId, "ACC-1", "View"); // no Debit permission

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner())
                .RequestAuthorizationAsync(Request(customerId, operation: "Debit")));

        Assert.Equal(AuthorizationDenialReasons.OperationNotPermittedByMandate, ex.ReasonCode);
    }

    [Fact]
    public async Task RequestAuthorizationAsync_Denies_WhenTheKycTierDoesNotAllowTheOperation()
    {
        // Tier1's placeholder seed only allows "View" (SeedPlaceholderKycTierPolicies migration).
        var customerId = await CreateCustomerAsync(kycTier: "Tier1");
        await AttachMandateAsync(customerId, "ACC-1", "Debit");

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner())
                .RequestAuthorizationAsync(Request(customerId, operation: "Debit")));

        Assert.Equal(AuthorizationDenialReasons.OperationNotEligibleForKycTier, ex.ReasonCode);
    }

    [Fact]
    public async Task RequestAuthorizationAsync_Denies_ForACustomerWhoseKycIsStillPending()
    {
        var customerId = await CreateCustomerAsync(kycTier: null);
        await AttachMandateAsync(customerId, "ACC-1", "Debit");

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner()).RequestAuthorizationAsync(Request(customerId)));

        Assert.Equal(AuthorizationDenialReasons.CustomerNotVerified, ex.ReasonCode);
    }

    // DESIGN.md Appendix A: "a revoked mandate cannot obtain a new grant."
    [Fact]
    public async Task RequestAuthorizationAsync_Denies_AfterTheMandateHasBeenRevoked()
    {
        var customerId = await CreateCustomerAsync();
        var mandateId = await AttachMandateAsync(customerId, "ACC-1", "Debit");

        await using (var revokeCtx = fixture.CreateContext())
        {
            await new MandateService(revokeCtx, TestAudit.For(revokeCtx), TestCaller.Default)
                .RevokeMandateAsync(mandateId, new RevokeMandateRequest("no longer authorized"));
        }

        await using var ctx = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<AuthorizationDeniedException>(
            () => new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner()).RequestAuthorizationAsync(Request(customerId)));

        Assert.Equal(AuthorizationDenialReasons.NoActiveMandate, ex.ReasonCode);
    }

    // Appendix A: "any changed account, amount, currency, operation or idempotency key causes
    // rejection" - the simple in-test check the task file allows, since no LedgerApi verifier exists yet.
    [Fact]
    public async Task TamperedGrant_FailsSignatureVerification()
    {
        var customerId = await CreateCustomerAsync();
        await AttachMandateAsync(customerId, "ACC-1", "Debit");

        await using var ctx = fixture.CreateContext();
        var response = await new AuthorizationService(ctx, TestAudit.For(ctx), CreateGrantSigner())
            .RequestAuthorizationAsync(Request(customerId, amount: 5000m));

        var parts = response.Grant.Split('.');
        Assert.Equal(3, parts.Length);

        // Flip one character in the payload segment - amount/claims live there - without re-signing.
        var payloadChars = parts[1].ToCharArray();
        var flipIndex = payloadChars.Length / 2;
        payloadChars[flipIndex] = payloadChars[flipIndex] == 'A' ? 'B' : 'A';
        var tampered = string.Join('.', parts[0], new string(payloadChars), parts[2]);

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(tampered, PublicKeyValidationParameters());

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task ExpiredGrant_FailsValidation()
    {
        var customerId = await CreateCustomerAsync();
        var mandateId = await AttachMandateAsync(customerId, "ACC-1", "Debit");

        // Sign directly (bypassing AuthorizationService's real-time clock) to produce an
        // already-expired grant, proving a verifier configured with no clock-skew tolerance beyond the
        // design's "about 5 seconds" (ARCHITECTURE.md Section 9) rejects it.
        var signer = CreateGrantSigner();
        var issuedInThePast = DateTimeOffset.UtcNow.AddMinutes(-5);
        var grant = signer.Sign(new GrantClaims(
            Guid.NewGuid().ToString(), customerId, "Debit", "ACC-1", "ACC-2", "5000", "NGN", "idem-expired",
            mandateId, issuedInThePast, issuedInThePast.AddSeconds(30)));

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(grant.Token, PublicKeyValidationParameters());

        Assert.False(result.IsValid);
    }
}
