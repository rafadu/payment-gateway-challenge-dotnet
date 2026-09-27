using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// The backing source of merchant credentials (MongoDB in this build). Sits behind
/// <see cref="ICredentialCache"/>, which is the only thing the token-issuance flow depends on —
/// this port is what the cache reads through to on a miss.
/// </summary>
public interface ICredentialStore
{
    /// <summary>Returns the credential for <paramref name="clientId"/>, or <c>null</c> if unknown.</summary>
    Task<MerchantCredential?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken = default);
}
