using FluentAssertions;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

public class PaymentsRepositoryTests
{
    private readonly PaymentsRepository _repository = new();

    private static Payment APayment(Guid id) => new()
    {
        Id = id,
        Status = PaymentStatus.Authorized,
        CardNumberLastFour = "1111",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 1000
    };

    [Fact]
    public void Get_returns_a_previously_added_payment()
    {
        var payment = APayment(Guid.NewGuid());
        _repository.Add(payment);

        _repository.Get(payment.Id).Should().BeSameAs(payment);
    }

    [Fact]
    public void Get_returns_null_for_an_unknown_id()
    {
        _repository.Add(APayment(Guid.NewGuid()));

        _repository.Get(Guid.NewGuid()).Should().BeNull();
    }

    [Fact]
    public void Add_with_a_duplicate_id_replaces_the_existing_payment()
    {
        var id = Guid.NewGuid();
        var original = APayment(id);
        var replacement = APayment(id);

        _repository.Add(original);
        _repository.Add(replacement);

        _repository.Get(id).Should().BeSameAs(replacement);
    }

    [Fact]
    public void Add_keeps_distinct_payments_retrievable_by_their_own_id()
    {
        var first = APayment(Guid.NewGuid());
        var second = APayment(Guid.NewGuid());

        _repository.Add(first);
        _repository.Add(second);

        _repository.Get(first.Id).Should().BeSameAs(first);
        _repository.Get(second.Id).Should().BeSameAs(second);
    }
}
