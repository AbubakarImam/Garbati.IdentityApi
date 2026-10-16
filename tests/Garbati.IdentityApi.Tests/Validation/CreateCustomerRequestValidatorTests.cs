using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class CreateCustomerRequestValidatorTests
{
    private readonly CreateCustomerRequestValidator _validator = new();

    private static CreateCustomerRequest Valid() =>
        new("Ada Lovelace", "NG", null, "ada@example.com", "+2348000000000", "order-42");

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

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenIdempotencyKeyIsMissing(string key)
    {
        var errors = _validator.Validate(Valid() with { IdempotencyKey = key });

        Assert.Contains(errors, e => e.Contains("IdempotencyKey"));
    }

    [Fact]
    public void Fails_WhenIdempotencyKeyHasAnInvalidCharacter()
    {
        var errors = _validator.Validate(Valid() with { IdempotencyKey = "has a space" });

        Assert.Contains(errors, e => e.Contains("IdempotencyKey"));
    }

    [Fact]
    public void Fails_WhenIdempotencyKeyIsTooLong()
    {
        var errors = _validator.Validate(Valid() with { IdempotencyKey = new string('a', 129) });

        Assert.Contains(errors, e => e.Contains("IdempotencyKey"));
    }
}
