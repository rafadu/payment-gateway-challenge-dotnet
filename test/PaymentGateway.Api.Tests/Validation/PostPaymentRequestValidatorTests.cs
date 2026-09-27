using FluentValidation.TestHelper;

using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Validation;

namespace PaymentGateway.Api.Tests.Validation;

public class PostPaymentRequestValidatorTests
{
    private static readonly string[] SupportedCurrencies = ["GBP", "USD", "EUR"];

    // Fixed "now" so the expiry-in-future rule is deterministic.
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);

    private readonly PostPaymentRequestValidator _validator =
        new(SupportedCurrencies, new FixedTimeProvider(Now));

    private static PostPaymentRequest ValidRequest() => new()
    {
        CardNumber = "4111111111111111",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 1000,
        Cvv = "123"
    };

    [Fact]
    public void Valid_request_passes()
    {
        _validator.TestValidate(ValidRequest()).ShouldNotHaveAnyValidationErrors();
    }

    // ---- Card number ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Card_number_is_required(string? cardNumber)
    {
        var request = ValidRequest();
        request.CardNumber = cardNumber;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.CardNumber);
    }

    [Theory]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("411111111111111a")]
    public void Card_number_must_be_numeric(string cardNumber)
    {
        var request = ValidRequest();
        request.CardNumber = cardNumber;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.CardNumber);
    }

    [Theory]
    [InlineData("1234567890123")]      // 13 digits — too short
    [InlineData("12345678901234567890")] // 20 digits — too long
    public void Card_number_must_be_14_to_19_digits(string cardNumber)
    {
        var request = ValidRequest();
        request.CardNumber = cardNumber;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.CardNumber);
    }

    [Theory]
    [InlineData("12345678901234")]        // 14 digits — lower boundary
    [InlineData("1234567890123456789")]   // 19 digits — upper boundary
    public void Card_number_at_length_boundaries_is_valid(string cardNumber)
    {
        var request = ValidRequest();
        request.CardNumber = cardNumber;

        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.CardNumber);
    }

    // ---- Expiry month ----

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Expiry_month_must_be_1_to_12(int month)
    {
        var request = ValidRequest();
        request.ExpiryMonth = month;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.ExpiryMonth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12)]
    public void Expiry_month_at_boundaries_is_valid(int month)
    {
        var request = ValidRequest();
        request.ExpiryMonth = month;
        request.ExpiryYear = 2030;

        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.ExpiryMonth);
    }

    // ---- Expiry in the future (month + year combined) ----

    [Fact]
    public void Expiry_in_the_past_is_invalid()
    {
        var request = ValidRequest();
        request.ExpiryMonth = 5; // before June 2026
        request.ExpiryYear = 2026;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.ExpiryYear);
    }

    [Fact]
    public void Expiry_in_the_current_month_is_valid()
    {
        var request = ValidRequest();
        request.ExpiryMonth = 6; // June 2026 — card valid through end of month
        request.ExpiryYear = 2026;

        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.ExpiryYear);
    }

    [Fact]
    public void Expiry_in_the_future_is_valid()
    {
        var request = ValidRequest();
        request.ExpiryMonth = 1;
        request.ExpiryYear = 2027;

        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.ExpiryYear);
    }

    // ---- Currency ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Currency_is_required(string? currency)
    {
        var request = ValidRequest();
        request.Currency = currency;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDD")]
    public void Currency_must_be_three_characters(string currency)
    {
        var request = ValidRequest();
        request.Currency = currency;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Fact]
    public void Currency_must_be_in_the_supported_list()
    {
        var request = ValidRequest();
        request.Currency = "JPY";

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Theory]
    [InlineData("GBP")]
    [InlineData("USD")]
    [InlineData("EUR")]
    public void Supported_currency_is_valid(string currency)
    {
        var request = ValidRequest();
        request.Currency = currency;

        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.Currency);
    }

    // ---- Amount ----

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Amount_must_be_greater_than_zero(int amount)
    {
        var request = ValidRequest();
        request.Amount = amount;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Amount);
    }

    [Fact]
    public void Positive_amount_is_valid()
    {
        var request = ValidRequest();
        request.Amount = 1;

        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.Amount);
    }

    // ---- CVV ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Cvv_is_required(string? cvv)
    {
        var request = ValidRequest();
        request.Cvv = cvv;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Cvv);
    }

    [Theory]
    [InlineData("12a")]
    [InlineData("ab")]
    public void Cvv_must_be_numeric(string cvv)
    {
        var request = ValidRequest();
        request.Cvv = cvv;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Cvv);
    }

    [Theory]
    [InlineData("12")]    // too short
    [InlineData("12345")] // too long
    public void Cvv_must_be_3_or_4_digits(string cvv)
    {
        var request = ValidRequest();
        request.Cvv = cvv;

        _validator.TestValidate(request).ShouldHaveValidationErrorFor(x => x.Cvv);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("1234")]
    public void Cvv_at_length_boundaries_is_valid(string cvv)
    {
        var request = ValidRequest();
        request.Cvv = cvv;

        _validator.TestValidate(request).ShouldNotHaveValidationErrorFor(x => x.Cvv);
    }

    /// <summary>Minimal <see cref="TimeProvider"/> test double returning a fixed instant.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
