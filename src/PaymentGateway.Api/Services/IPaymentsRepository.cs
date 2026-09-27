using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Stores and retrieves bank-adjudicated payments. The MongoDB-backed implementation
/// (<see cref="MongoPaymentsRepository"/>) is the production registration; the in-memory
/// <see cref="InMemoryPaymentsRepository"/> exists as a unit-test fixture. Both are
/// registered as singletons and safe under concurrent access.
/// </summary>
public interface IPaymentsRepository
{
    /// <summary>
    /// Persists a payment, keyed by its <see cref="Payment.Id"/>, replacing any existing payment
    /// stored under the same id. In normal operation ids are gateway-generated GUIDs, so a
    /// collision does not occur; this replace-on-duplicate behaviour is the store's defined
    /// contract rather than a scenario the callers rely on.
    /// </summary>
    Task AddAsync(Payment payment, CancellationToken cancellationToken = default);

    /// <summary>Returns the payment with the given id, or <c>null</c> if none is stored.</summary>
    Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}