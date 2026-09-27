using System.Collections.Concurrent;

using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// In-memory <see cref="IPaymentsRepository"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// so it is safe under the concurrent request handling ASP.NET Core performs by default (the
/// scaffold's plain <c>List&lt;T&gt;</c> was not). Registered as a singleton, so all requests share
/// one store for the lifetime of the process.
/// </summary>
public class PaymentsRepository : IPaymentsRepository
{
    private readonly ConcurrentDictionary<Guid, Payment> _payments = new();

    public void Add(Payment payment) => _payments[payment.Id] = payment;

    public Payment? Get(Guid id) => _payments.TryGetValue(id, out var payment) ? payment : null;
}
