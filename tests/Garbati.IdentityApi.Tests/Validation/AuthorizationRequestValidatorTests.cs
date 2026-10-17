using Garbati.IdentityApi.Contracts.Requests;
using Garbati.IdentityApi.Validation;

namespace Garbati.IdentityApi.Tests.Validation;

public class AuthorizationRequestValidatorTests
{
    private readonly AuthorizationRequestValidator _validator = new();

    private static AuthorizationRequest Valid() =>
        new("CUS-ABCDEFGHJKLMNPQ", "Debit", "ACC-1", "ACC-2", 100m, "NGN", "order-1");

    [Fact]
    public void Validate_Passes_ForAValidRequest()
    {
        Assert.Empty(_validator.Validate(Valid()));
    }

    [Fact]
    public void Validate_RequiresRequestedBy()
    {
        var errors = _validator.Validate(Valid() with { RequestedBy = "" });

        Assert.Contains(errors, e => e.Contains("RequestedBy"));
    }

    [Fact]
    public void Validate_RejectsAnUnrecognizedOperation()
    {
        var errors = _validator.Validate(Valid() with { Operation = "NotARealOperation" });

        Assert.Contains(errors, e => e.Contains("Operation"));
    }

    [Fact]
    public void Validate_RequiresDebitAccountId()
    {
        var errors = _validator.Validate(Valid() with { DebitAccountId = "" });

        Assert.Contains(errors, e => e.Contains("DebitAccountId"));
    }

    [Fact]
    public void Validate_RequiresCreditAccountId()
    {
        var errors = _validator.Validate(Valid() with { CreditAccountId = "" });

        Assert.Contains(errors, e => e.Contains("CreditAccountId"));
    }

    [Fact]
    public void Validate_RequiresAPositiveAmount()
    {
        var errors = _validator.Validate(Valid() with { Amount = 0m });

        Assert.Contains(errors, e => e.Contains("Amount"));
    }

    [Fact]
    public void Validate_RequiresAThreeLetterCurrency()
    {
        var errors = _validator.Validate(Valid() with { Currency = "N" });

        Assert.Contains(errors, e => e.Contains("Currency"));
    }

    [Fact]
    public void Validate_RequiresAnIdempotencyKey()
    {
        var errors = _validator.Validate(Valid() with { IdempotencyKey = "" });

        Assert.Contains(errors, e => e.Contains("IdempotencyKey"));
    }
}
