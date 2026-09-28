using FluentAssertions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Tests.Domain;

/// <summary>
/// Tests for <see cref="BankIntent.StartPending"/> — the factory that creates a new outbox
/// record in the Pending state. Pure: no I/O, no DI.
/// </summary>
public class BankIntentFactoryTests
{
    private static readonly DateTime FixedNow = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static BankIntentRequest ARequest() => new()
    {
        CardLastFour = "8877",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100
    };

    [Fact]
    public void StartPending_assigns_the_supplied_id_merchant_id_and_request_to_the_intent()
    {
        var id = Guid.NewGuid();
        var request = ARequest();

        var intent = BankIntent.StartPending(id, "merchant-42", request, FixedNow);

        intent.Id.Should().Be(id);
        intent.MerchantId.Should().Be("merchant-42");
        intent.Request.Should().BeSameAs(request);
    }

    [Fact]
    public void StartPending_stamps_CreatedAt_and_UpdatedAt_to_the_supplied_now()
    {
        var intent = BankIntent.StartPending(Guid.NewGuid(), "m", ARequest(), FixedNow);

        intent.CreatedAt.Should().Be(FixedNow);
        intent.UpdatedAt.Should().Be(FixedNow);
    }

    [Fact]
    public void StartPending_sets_Status_to_Pending_with_no_response_and_zero_attempts()
    {
        var intent = BankIntent.StartPending(Guid.NewGuid(), "m", ARequest(), FixedNow);

        intent.Status.Should().Be(BankIntentStatus.Pending);
        intent.Response.Should().BeNull();
        intent.Attempts.Should().Be(0);
    }
}
