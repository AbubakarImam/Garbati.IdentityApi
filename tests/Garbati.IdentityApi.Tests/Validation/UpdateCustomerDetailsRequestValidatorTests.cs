using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class UpdateCustomerDetailsRequestValidatorTests
{
    private readonly UpdateCustomerDetailsRequestValidator _validator = new();

    [Fact]
    public void Passes_WhenOnlyFullNameIsSupplied()
    {
        Assert.Empty(_validator.Validate(new UpdateCustomerDetailsRequest("Ada Lovelace", null, null)));
    }

    [Fact]
    public void Passes_WhenOnlyEmailIsSupplied()
    {
        Assert.Empty(_validator.Validate(new UpdateCustomerDetailsRequest(null, "ada@example.com", null)));
    }

    [Fact]
    public void Passes_WhenOnlyPhoneNumberIsSupplied()
    {
        Assert.Empty(_validator.Validate(new UpdateCustomerDetailsRequest(null, null, "+2348000000000")));
    }

    [Fact]
    public void Passes_WhenAllThreeFieldsAreSupplied()
    {
        Assert.Empty(_validator.Validate(new UpdateCustomerDetailsRequest("Ada Lovelace", "ada@example.com", "+2348000000000")));
    }

    [Fact]
    public void Fails_WhenEveryFieldIsNull()
    {
        var errors = _validator.Validate(new UpdateCustomerDetailsRequest(null, null, null));

        Assert.NotEmpty(errors);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenFullNameIsSuppliedButBlank(string fullName)
    {
        var errors = _validator.Validate(new UpdateCustomerDetailsRequest(fullName, null, null));

        Assert.Contains(errors, e => e.Contains("FullName"));
    }

    // QA review: null ("leave this field alone") and "" ("actively supplying a blank value") are not the
    // same instruction on a PATCH - a supplied-but-blank Email/PhoneNumber must be rejected the same way
    // FullName already was, not silently accepted and allowed to overwrite the stored value with "".
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenEmailIsSuppliedButBlank(string email)
    {
        var errors = _validator.Validate(new UpdateCustomerDetailsRequest(null, email, null));

        Assert.Contains(errors, e => e.Contains("Email"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Fails_WhenPhoneNumberIsSuppliedButBlank(string phoneNumber)
    {
        var errors = _validator.Validate(new UpdateCustomerDetailsRequest(null, null, phoneNumber));

        Assert.Contains(errors, e => e.Contains("PhoneNumber"));
    }

    [Fact]
    public void Fails_WhenEmailHasNoAtSign()
    {
        var errors = _validator.Validate(new UpdateCustomerDetailsRequest(null, "not-an-email", null));

        Assert.Contains(errors, e => e.Contains("Email"));
    }

    // PhoneNumber has no format check at all - mirrors CreateCustomerRequestValidator, which does not
    // validate PhoneNumber either (see CreateCustomerRequestValidatorTests: there is no
    // Fails_WhenPhoneNumberIsInvalid case because no such rule exists for creation).
    [Fact]
    public void Passes_WithAnyNonNullPhoneNumber_SinceCreationAppliesNoFormatCheckEither()
    {
        Assert.Empty(_validator.Validate(new UpdateCustomerDetailsRequest(null, null, "not-a-phone-number")));
    }
}
