using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class CustomerStatusChangeRequestValidatorTests
{
    private readonly CustomerStatusChangeRequestValidator _validator = new();

    [Fact]
    public void Passes_WhenReasonIsProvided()
    {
        Assert.Empty(_validator.Validate(new CustomerStatusChangeRequest("fraud review")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenReasonIsMissing(string? reason)
    {
        var errors = _validator.Validate(new CustomerStatusChangeRequest(reason));

        Assert.Contains(errors, e => e.Contains("Reason"));
    }
}
