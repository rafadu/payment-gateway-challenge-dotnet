using FluentAssertions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Tests.Domain;

/// <summary>
/// Tests for <see cref="BankIntentRequest.FromMerchantRequest"/> — the factory that produces a
/// PCI-safe snapshot of the merchant request for storage on the <see cref="BankIntent"/> outbox
/// record. Never carries PAN or CVV (ADR-0008 architecture-test safety net).
/// </summary>
public class BankIntentRequestFactoryTests
{
    private static PostPaymentRequest ARequest() => new()
    {
        CardNumber = "2222405343248877",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100,
        Cvv = "123"
    };

    [Fact]
    public void FromMerchantRequest_extracts_the_last_four_card_digits()
    {
        var snapshot = BankIntentRequest.FromMerchantRequest(ARequest());

        snapshot.CardLastFour.Should().Be("8877");
    }

    [Fact]
    public void FromMerchantRequest_preserves_a_leading_zero_in_the_last_four()
    {
        // Regression guard: the original scaffold typed CardNumberLastFour as int and silently dropped
        // a leading zero. The snapshot stays as a string and slices the source directly.
        var request = ARequest();
        request.CardNumber = "2222405343240007";

        var snapshot = BankIntentRequest.FromMerchantRequest(request);

        snapshot.CardLastFour.Should().Be("0007");
    }

    [Fact]
    public void FromMerchantRequest_carries_expiry_currency_and_amount_unchanged_from_the_request()
    {
        var request = ARequest();

        var snapshot = BankIntentRequest.FromMerchantRequest(request);

        snapshot.ExpiryMonth.Should().Be(request.ExpiryMonth);
        snapshot.ExpiryYear.Should().Be(request.ExpiryYear);
        snapshot.Currency.Should().Be(request.Currency);
        snapshot.Amount.Should().Be(request.Amount);
    }

    [Fact]
    public void FromMerchantRequest_does_not_carry_PAN_or_CVV()
    {
        // PCI safety net: the snapshot must hold last-four at most. This test is duplicated by the
        // ADR-0008 architecture test (which scans type members for sensitive names) but the
        // behavioral assertion is worth pinning here too — a future refactor that accidentally
        // widens the snapshot fails this test before the architecture test even runs.
        var snapshot = BankIntentRequest.FromMerchantRequest(ARequest());

        snapshot.GetType().GetProperties().Select(p => p.Name).Should().NotContain("CardNumber");
        snapshot.GetType().GetProperties().Select(p => p.Name).Should().NotContain("Cvv");
    }
}
