using Garbati.IdentityApi.Signing;
using Garbati.IdentityApi.Tests.Fixtures;
using Microsoft.Extensions.Hosting;

namespace Garbati.IdentityApi.Tests.Signing;

public class SigningKeyOptionsValidatorTests
{
    // A real, valid ES256 key that is NOT the known dev/test key - generated only for this test, to
    // prove the Production guard is specific (blocks the known key) and not overly broad (does not
    // block every key in Production).
    private const string OtherValidKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgNUAbFBPMAAMK/pXt
        Zkm2uGr7NlzrkBe5Bj5r/rQeCiShRANCAATkIzI41MI+AD7Ped1bFrJOkakevIAN
        ekYjHXsqa8lBX/DnFc6C5GLc4xKaEiE+YBHepQnvgfY1aoeOn2LAqdkG
        -----END PRIVATE KEY-----
        """;

    // A structurally valid EC private key, but on the P-384 curve, not ES256's required P-256 - used
    // only to prove the curve check.
    private const string WrongCurveKeyPem = """
        -----BEGIN PRIVATE KEY-----
        MIG2AgEAMBAGByqGSM49AgEGBSuBBAAiBIGeMIGbAgEBBDBBQ6JXFu5WT5pOsqq2
        irRYLel5q+4Tkk7AG7BhAts0DhYC9em2X5R66H2JevaVy3mhZANiAARPx2w4f9A6
        0lHwpve1CHETlYjIK5dqX2OSR6d/5yrjI7OiAPipRDPCgC9lh43svoKIbWy2/ODS
        CEZYR4kLugq30paZvUDBpQHBc7oXb69BdI9foAeRbQHsuSpFHk+Zp00=
        -----END PRIVATE KEY-----
        """;

    private static SigningKeyOptionsValidator Validator(string environmentName = "Development") =>
        new(new FakeHostEnvironment(environmentName));

    private static SigningKeyEntry DevKey() => new() { Kid = "identity-dev-key-2026-10", PrivateKeyPem = TestSigningKey.PrivateKeyPem };

    private static SigningKeyOptions Options(params SigningKeyEntry[] keys) => new()
    {
        ActiveKid = keys.Length > 0 ? keys[0].Kid : "",
        Keys = [.. keys],
    };

    [Fact]
    public void Passes_ForAValidKey()
    {
        var result = Validator().Validate(null, Options(DevKey()));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Fails_WhenNoKeysAreConfigured()
    {
        var result = Validator().Validate(null, Options());

        Assert.True(result.Failed);
        Assert.Contains("at least one signing key", result.FailureMessage);
    }

    [Fact]
    public void Fails_WhenPrivateKeyPemIsNotAValidEcKey()
    {
        var result = Validator().Validate(null, Options(new SigningKeyEntry { Kid = "bad", PrivateKeyPem = "not a pem" }));

        Assert.True(result.Failed);
        Assert.Contains("not a valid EC private key", result.FailureMessage);
    }

    [Fact]
    public void Fails_WhenTheKeyIsNotOnTheP256Curve()
    {
        var result = Validator().Validate(null, Options(new SigningKeyEntry { Kid = "wrong-curve", PrivateKeyPem = WrongCurveKeyPem }));

        Assert.True(result.Failed);
        Assert.Contains("not a P-256 (ES256) key", result.FailureMessage);
    }

    [Fact]
    public void Fails_ForADuplicateKid()
    {
        var result = Validator().Validate(null, Options(DevKey(), DevKey()));

        Assert.True(result.Failed);
        Assert.Contains("Duplicate", result.FailureMessage);
    }

    [Fact]
    public void Fails_WhenActiveKidIsMissing()
    {
        var options = Options(DevKey());
        options.ActiveKid = "";

        var result = Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("ActiveKid is required", result.FailureMessage);
    }

    [Fact]
    public void Fails_WhenActiveKidDoesNotMatchAnyConfiguredKey()
    {
        var options = Options(DevKey());
        options.ActiveKid = "some-other-kid";

        var result = Validator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains("does not match any configured key", result.FailureMessage);
    }

    // The owner's explicit condition for #TBD-15 (docs/DESIGN.md Appendix C): the dev/test key material
    // committed openly in this repository must never be configured in Production.
    [Fact]
    public void Fails_WhenTheDevKeyIsConfiguredInProduction()
    {
        var result = Validator("Production").Validate(null, Options(DevKey()));

        Assert.True(result.Failed);
        Assert.Contains("known dev/test signing key", result.FailureMessage);
        Assert.Contains("Production", result.FailureMessage);
    }

    [Fact]
    public void Fails_WhenTheTestKeyIsConfiguredInProduction()
    {
        var testKey = new SigningKeyEntry { Kid = TestSigningKey.Kid, PrivateKeyPem = TestSigningKey.PrivateKeyPem };

        var result = Validator("Production").Validate(null, Options(testKey));

        Assert.True(result.Failed);
        Assert.Contains("known dev/test signing key", result.FailureMessage);
    }

    // Relabeling the Kid must not defeat the check - it compares key MATERIAL, not the Kid string.
    [Fact]
    public void Fails_WhenTheDevKeyIsConfiguredInProduction_EvenUnderADifferentKid()
    {
        var relabeled = new SigningKeyEntry { Kid = "looks-like-a-real-production-key", PrivateKeyPem = TestSigningKey.PrivateKeyPem };

        var result = Validator("Production").Validate(null, Options(relabeled));

        Assert.True(result.Failed);
        Assert.Contains("known dev/test signing key", result.FailureMessage);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void Passes_WhenTheDevOrTestKeyIsConfigured_OutsideProduction(string environmentName)
    {
        var result = Validator(environmentName).Validate(null, Options(DevKey()));

        Assert.True(result.Succeeded);
    }

    // Proves the Production guard is specific to the known dev/test key, not a blanket refusal of every
    // key in Production.
    [Fact]
    public void Passes_InProduction_ForAKeyThatIsNotTheKnownDevOrTestKey()
    {
        var result = Validator("Production").Validate(null, Options(new SigningKeyEntry { Kid = "prod-key", PrivateKeyPem = OtherValidKeyPem }));

        Assert.True(result.Succeeded);
    }
}
