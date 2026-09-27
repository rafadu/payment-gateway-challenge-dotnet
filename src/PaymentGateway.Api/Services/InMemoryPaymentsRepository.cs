using System.Collections.Concurrent;

using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// In-memory <see cref="IPaymentsRepository"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Used by unit tests as a fake — production code uses <see cref="MongoPaymentsRepository"/>.
/// Singleton — it owns the dictionary, no per-request state.
/// </summary>
public sealed class InMemoryPaymentsRepository : IPaymentsRepository
{
    private readonly ConcurrentDictionary<Guid, Payment> _payments = new();

    public Task AddAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        _payments[payment.Id] = payment;
        return Task.CompletedTask;
    }

    public Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_payments.TryGetValue(id, out var payment) ? payment : null);
}