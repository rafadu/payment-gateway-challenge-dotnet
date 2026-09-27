using FluentAssertions;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

public class InMemoryPaymentsRepositoryTests
{
    private readonly InMemoryPaymentsRepository _repository = new();

    private static Payment APayment(Guid id) => new()
    {
        Id = id,
        MerchantId = "merchant-42",
        Status = PaymentStatus.Authorized,
        CardNumberLastFour = "1111",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 1000
    };

    [Fact]
    public async Task GetAsync_returns_a_previously_added_payment()
    {
        var payment = APayment(Guid.NewGuid());
        await _repository.AddAsync(payment);

        (await _repository.GetAsync(payment.Id)).Should().BeSameAs(payment);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_an_unknown_id()
    {
        await _repository.AddAsync(APayment(Guid.NewGuid()));

        (await _repository.GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_with_a_duplicate_id_replaces_the_existing_payment()
    {
        var id = Guid.NewGuid();
        var original = APayment(id);
        var replacement = APayment(id);

        await _repository.AddAsync(original);
        await _repository.AddAsync(replacement);

        (await _repository.GetAsync(id)).Should().BeSameAs(replacement);
    }

    [Fact]
    public async Task AddAsync_keeps_distinct_payments_retrievable_by_their_own_id()
    {
        var first = APayment(Guid.NewGuid());
        var second = APayment(Guid.NewGuid());

        await _repository.AddAsync(first);
        await _repository.AddAsync(second);

        (await _repository.GetAsync(first.Id)).Should().BeSameAs(first);
        (await _repository.GetAsync(second.Id)).Should().BeSameAs(second);
    }
}