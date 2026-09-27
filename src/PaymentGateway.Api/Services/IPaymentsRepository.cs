using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Stores and retrieves bank-adjudicated payments. The implementation must be safe for the
/// concurrent request handling ASP.NET Core performs by default.
/// </summary>
public interface IPaymentsRepository
{
    /// <summary>
    /// Persists a payment, keyed by its <see cref="Payment.Id"/>, replacing any existing payment
    /// stored under the same id. In normal operation ids are gateway-generated GUIDs, so a
    /// collision does not occur; this replace-on-duplicate behaviour is the store's defined
    /// contract rather than a scenario the callers rely on.
    /// </summary>
    void Add(Payment payment);

    /// <summary>Returns the payment with the given id, or <c>null</c> if none is stored.</summary>
    Payment? Get(Guid id);
}
