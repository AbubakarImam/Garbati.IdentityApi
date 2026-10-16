using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class CreateCustomerRequestValidatorTests
{
    private readonly CreateCustomerRequestValidator _validator = new();

    private static CreateCustomerRequest Valid() => new("Ada Lovelace", "NG", null, "ada@example.com", "+2348000000000");

    [Fact]
    public void Passes_ForAValidRequest()
    {
        Assert.Empty(_validator.Validate(Valid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenFullNameIsMissing(string fullName)
    {
        var errors = _validator.Validate(Valid() with { FullName = fullName });

        Assert.Contains(errors, e => e.Contains("FullName"));
    }

    [Fact]
    public void Fails_WhenJurisdictionCodeIsMissing()
    {
        var errors = _validator.Validate(Valid() with { JurisdictionCode = "" });

        Assert.Contains(errors, e => e.Contains("JurisdictionCode"));
    }

    [Fact]
    public void Fails_WhenEmailHasNoAtSign()
    {
        var errors = _validator.Validate(Valid() with { Email = "not-an-email" });

        Assert.Contains(errors, e => e.Contains("Email"));
    }

    [Fact]
    public void Passes_WhenEmailIsAbsent()
    {
        Assert.Empty(_validator.Validate(Valid() with { Email = null }));
    }
}
