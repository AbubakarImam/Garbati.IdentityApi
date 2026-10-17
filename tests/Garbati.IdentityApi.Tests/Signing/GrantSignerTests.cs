using System.Security.Cryptography;
using Garbati.IdentityApi.Signing;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Garbati.IdentityApi.Tests.Signing;

public class GrantSignerTests
{
    private static GrantSigner CreateSigner() => new(Options.Create(new SigningKeyOptions
    {
        ActiveKid = TestSigningKey.Kid,
        Keys = [new SigningKeyEntry { Kid = TestSigningKey.Kid, PrivateKeyPem = TestSigningKey.PrivateKeyPem }],
    }));

    private static GrantClaims Claims(string jti) => new(
        Jti: jti,
        Sub: "CUS-TEST",
        Operation: "Debit",
        DebitAccountId: "ACC-1",
        CreditAccountId: "ACC-2",
        Amount: "100.00",
        Currency: "NGN",
        IdempotencyKey: $"idem-{jti}",
        MandateId: "MND-TEST",
        IssuedAt: DateTimeOffset.UtcNow,
        ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(30));

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
    public void Sign_ReturnsAValidGrant()
    {
        var signer = CreateSigner();

        var grant = signer.Sign(Claims("single-call"));

        Assert.NotEmpty(grant.Token);
        Assert.Equal(TestSigningKey.Kid, grant.Kid);
    }

    // The real point of this test: GrantSigner.Sign() builds a fresh ECDsa per call specifically because
    // .NET does not guarantee ECDsa's instance methods are safe to call concurrently from multiple
    // threads (see GrantSigner's own comment). A shared, long-lived ECDsa instance under concurrent
    // signing has been a documented source of real crashes/corruption in .NET programs. This proves the
    // fix actually holds under real concurrent load - not just that the code compiles - by firing many
    // signing calls from the thread pool at once and checking every resulting grant is well-formed,
    // unique, and independently verifiable.
    [Fact]
    public async Task Sign_FromManyThreadsConcurrently_ProducesOnlyValidDistinctGrants()
    {
        var signer = CreateSigner();
        const int concurrentSigners = 100;

        var tokens = await Task.WhenAll(Enumerable.Range(0, concurrentSigners)
            .Select(i => Task.Run(() => signer.Sign(Claims($"concurrent-{i}")).Token)));

        // No exception escaped Task.WhenAll, and every call produced a distinct, well-formed token.
        Assert.Equal(concurrentSigners, tokens.Length);
        Assert.Equal(concurrentSigners, tokens.Distinct().Count());

        var handler = new JsonWebTokenHandler();
        var validationParameters = PublicKeyValidationParameters();
        foreach (var token in tokens)
        {
            var result = await handler.ValidateTokenAsync(token, validationParameters);
            Assert.True(result.IsValid, result.Exception?.ToString());
        }
    }
}
