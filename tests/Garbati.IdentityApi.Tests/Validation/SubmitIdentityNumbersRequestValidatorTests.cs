using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class SubmitIdentityNumbersRequestValidatorTests
{
    private readonly SubmitIdentityNumbersRequestValidator _validator = new();

    private const string ValidBvn = "12345678901";
    private const string ValidNin = "10987654321";

    [Fact]
    public void Passes_WhenOnlyBvnIsSupplied_AndValid()
    {
        Assert.Empty(_validator.Validate(new SubmitIdentityNumbersRequest(ValidBvn, null)));
    }

    [Fact]
    public void Passes_WhenOnlyNinIsSupplied_AndValid()
    {
        Assert.Empty(_validator.Validate(new SubmitIdentityNumbersRequest(null, ValidNin)));
    }

    [Fact]
    public void Passes_WhenBothAreSupplied_AndValid()
    {
        Assert.Empty(_validator.Validate(new SubmitIdentityNumbersRequest(ValidBvn, ValidNin)));
    }

    [Fact]
    public void Fails_WhenBothFieldsAreNull()
    {
        var errors = _validator.Validate(new SubmitIdentityNumbersRequest(null, null));

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains("Bvn") && e.Contains("Nin"));
    }

    [Theory]
    [InlineData("1234567890")] // 10 digits - too short
    [InlineData("123456789012")] // 12 digits - too long
    [InlineData("1234567890a")] // contains a letter
    [InlineData("1234-567890")] // contains a symbol
    public void Fails_WhenBvnIsNotExactlyElevenDigits(string invalidBvn)
    {
        var errors = _validator.Validate(new SubmitIdentityNumbersRequest(invalidBvn, null));

        Assert.Contains(errors, e => e.Contains("Bvn") && e.Contains("11 digits"));
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("123456789012")]
    [InlineData("1234567890a")]
    [InlineData("1234-567890")]
    public void Fails_WhenNinIsNotExactlyElevenDigits(string invalidNin)
    {
        var errors = _validator.Validate(new SubmitIdentityNumbersRequest(null, invalidNin));

        Assert.Contains(errors, e => e.Contains("Nin") && e.Contains("11 digits"));
    }

    [Fact]
    public void Fails_ForBothFields_WhenBothAreSuppliedButInvalid()
    {
        var errors = _validator.Validate(new SubmitIdentityNumbersRequest("short", "alsoshort"));

        Assert.Contains(errors, e => e.Contains("Bvn"));
        Assert.Contains(errors, e => e.Contains("Nin"));
    }

    // Unicode decimal digits (e.g. Arabic-Indic) must not pass as "11 digits" - the task scopes this to
    // ASCII digits specifically (IdentityNumberRule uses [0-9], not \d, for exactly this reason).
    [Fact]
    public void Fails_WhenBvnContainsNonAsciiDigits()
    {
        var errors = _validator.Validate(new SubmitIdentityNumbersRequest("١٢٣٤٥٦٧٨٩٠١", null));

        Assert.Contains(errors, e => e.Contains("Bvn"));
    }
}
