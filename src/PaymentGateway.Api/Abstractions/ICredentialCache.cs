using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// A read-through, in-memory cache of merchant credentials over <see cref="ICredentialStore"/>
/// (ADR-0010). It exists so merchant lookups on the token hot path avoid a datastore round-trip
/// per request, while still falling back to the store at least once per TTL so a rotated or
/// revoked credential is eventually picked up. This is the seam the Redis evolution (ADR-0011)
/// would swap behind, so callers depend only on this interface.
/// </summary>
public interface ICredentialCache
{
    /// <summary>
    /// Returns the credential for <paramref name="clientId"/>, or <c>null</c> if unknown. A live
    /// cached entry is served directly; an expired or absent entry is (re)fetched from the store.
    /// </summary>
    Task<MerchantCredential?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default);
}
