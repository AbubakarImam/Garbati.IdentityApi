using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class RevokeMandateRequestValidatorTests
{
    private readonly RevokeMandateRequestValidator _validator = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RequiresAReason(string? reason)
    {
        var errors = _validator.Validate(new RevokeMandateRequest(reason));

        Assert.Contains(errors, e => e.Contains("Reason"));
    }

    [Fact]
    public void Validate_Passes_WithAReason()
    {
        var errors = _validator.Validate(new RevokeMandateRequest("customer requested removal"));

        Assert.Empty(errors);
    }
}
