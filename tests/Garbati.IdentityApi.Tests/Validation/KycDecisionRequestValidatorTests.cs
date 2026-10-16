using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class KycDecisionRequestValidatorTests
{
    private readonly KycDecisionRequestValidator _validator = new();

    private static KycDecisionRequest Valid() =>
        new("Verified", "Tier1", "BVN", "test-provider", "ref-1", "OK", DateTimeOffset.UtcNow.AddYears(1));

    [Fact]
    public void Passes_ForAValidVerifiedDecision()
    {
        Assert.Empty(_validator.Validate(Valid()));
    }

    [Fact]
    public void Passes_ForAPendingDecisionWithNoTier()
    {
        var errors = _validator.Validate(Valid() with { KycStatus = "Pending", KycTier = null });

        Assert.Empty(errors);
    }

    [Fact]
    public void Fails_ForAnUnrecognizedKycStatus()
    {
        var errors = _validator.Validate(Valid() with { KycStatus = "SortOfVerified" });

        Assert.Contains(errors, e => e.Contains("SortOfVerified"));
    }

    [Fact]
    public void Fails_WhenVerifiedWithNoTier()
    {
        var errors = _validator.Validate(Valid() with { KycTier = null });

        Assert.Contains(errors, e => e.Contains("KycTier"));
    }

    [Fact]
    public void Fails_WhenVerificationTypeIsMissing()
    {
        var errors = _validator.Validate(Valid() with { VerificationType = " " });

        Assert.Contains(errors, e => e.Contains("VerificationType"));
    }

    [Fact]
    public void Fails_WhenProviderIsMissing()
    {
        var errors = _validator.Validate(Valid() with { Provider = "" });

        Assert.Contains(errors, e => e.Contains("Provider"));
    }
}
