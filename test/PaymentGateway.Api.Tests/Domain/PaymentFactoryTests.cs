using FluentAssertions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Tests.Domain;

/// <summary>
/// Tests for <see cref="Payment.FromBankOutcome"/> — the domain factory that turns a bank-adjudicated
/// outcome into a persisted <see cref="Payment"/>. Pure: no I/O, no DI, no mocks.
/// </summary>
public class PaymentFactoryTests
{
    private const string MerchantId = "merchant-42";

    private static PostPaymentRequest ARequest() => new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 4,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    private static BankPaymentResponse Authorized() => new()
    {
        Authorized = true,
        AuthorizationCode = "auth-code"
    };

    private static BankPaymentResponse Declined() => new()
    {
        Authorized = false
    };

    [Fact]
    public void FromBankOutcome_maps_an_authorized_response_to_Authorized_status()
    {
        var payment = Payment.FromBankOutcome(MerchantId, ARequest(), Authorized());

        payment.Status.Should().Be(PaymentStatus.Authorized);
    }

    [Fact]
    public void FromBankOutcome_maps_a_declined_response_to_Declined_status()
    {
        var payment = Payment.FromBankOutcome(MerchantId, ARequest(), Declined());

        payment.Status.Should().Be(PaymentStatus.Declined);
    }

    [Fact]
    public void FromBankOutcome_stamps_the_calling_merchant_id_on_the_payment()
    {
        var payment = Payment.FromBankOutcome(MerchantId, ARequest(), Authorized());

        payment.MerchantId.Should().Be(MerchantId);
    }

    [Fact]
    public void FromBankOutcome_keeps_only_the_last_four_card_digits()
    {
        var payment = Payment.FromBankOutcome(MerchantId, ARequest(), Authorized());

        payment.CardNumberLastFour.Should().Be("8877");
    }

    [Fact]
    public void FromBankOutcome_preserves_a_leading_zero_in_the_last_four()
    {
        // Regression guard: the original scaffold typed CardNumberLastFour as int, which silently
        // dropped a leading zero. Last-four is a string in the domain model; the factory must not
        // round-trip it through a numeric type on its way out.
        var request = ARequest();
        request.CardNumber = "2222405343240007";

        var payment = Payment.FromBankOutcome(MerchantId, request, Authorized());

        payment.CardNumberLastFour.Should().Be("0007");
    }

    [Fact]
    public void FromBankOutcome_carries_the_expiry_currency_and_amount_unchanged_from_the_request()
    {
        var request = ARequest();
        var payment = Payment.FromBankOutcome(MerchantId, request, Authorized());

        payment.ExpiryMonth.Should().Be(request.ExpiryMonth);
        payment.ExpiryYear.Should().Be(request.ExpiryYear);
        payment.Currency.Should().Be(request.Currency);
        payment.Amount.Should().Be(request.Amount);
    }

    [Fact]
    public void FromBankOutcome_assigns_a_fresh_non_empty_id_each_call()
    {
        var first = Payment.FromBankOutcome(MerchantId, ARequest(), Authorized());
        var second = Payment.FromBankOutcome(MerchantId, ARequest(), Authorized());

        first.Id.Should().NotBeEmpty();
        second.Id.Should().NotBeEmpty();
        first.Id.Should().NotBe(second.Id);
    }
}
