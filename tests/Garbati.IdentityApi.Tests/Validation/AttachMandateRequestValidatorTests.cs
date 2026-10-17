using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class AttachMandateRequestValidatorTests
{
    private readonly AttachMandateRequestValidator _validator = new();

    [Fact]
    public void Validate_Passes_ForAValidRequest()
    {
        var errors = _validator.Validate(new AttachMandateRequest("ACC-1", ["View", "Debit"]));

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_RequiresLedgerAccountId(string? ledgerAccountId)
    {
        var errors = _validator.Validate(new AttachMandateRequest(ledgerAccountId!, ["View"]));

        Assert.Contains(errors, e => e.Contains("LedgerAccountId"));
    }

    [Fact]
    public void Validate_RequiresAtLeastOneOperation()
    {
        var errors = _validator.Validate(new AttachMandateRequest("ACC-1", []));

        Assert.Contains(errors, e => e.Contains("Operation"));
    }

    [Fact]
    public void Validate_RejectsAnUnrecognizedOperation()
    {
        var errors = _validator.Validate(new AttachMandateRequest("ACC-1", ["NotARealOperation"]));

        Assert.Contains(errors, e => e.Contains("NotARealOperation"));
    }
}
