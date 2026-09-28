using FluentAssertions;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Tests.Domain;

/// <summary>
/// Tests for <see cref="BankPaymentRequest.FromMerchantRequest"/> — the wire-format factory that
/// turns a merchant-facing <see cref="PostPaymentRequest"/> into the bank's snake_case request
/// DTO. Pure: no I/O, no DI, no mocks.
/// </summary>
public class BankPaymentRequestFactoryTests
{
    private static PostPaymentRequest ARequest() => new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 4,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    [Fact]
    public void FromMerchantRequest_sends_the_full_card_number_and_MM_yyyy_expiry_to_the_bank()
    {
        var sent = BankPaymentRequest.FromMerchantRequest(ARequest());

        sent.CardNumber.Should().Be("2222405343248877");
        sent.ExpiryDate.Should().Be("04/2030");
        sent.Currency.Should().Be("GBP");
        sent.Amount.Should().Be(100);
        sent.Cvv.Should().Be("123");
    }

    [Theory]
    [InlineData(4, 2030, "04/2030")]    // single-digit month zero-padded
    [InlineData(12, 2030, "12/2030")]   // two-digit month not over-padded; year not truncated to yy
    public void FromMerchantRequest_formats_the_expiry_as_MM_yyyy(int month, int year, string expected)
    {
        var request = ARequest();
        request.ExpiryMonth = month;
        request.ExpiryYear = year;

        var sent = BankPaymentRequest.FromMerchantRequest(request);

        sent.ExpiryDate.Should().Be(expected);
    }
}
