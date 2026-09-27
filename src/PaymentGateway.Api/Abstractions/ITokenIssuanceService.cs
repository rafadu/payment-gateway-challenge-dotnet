using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// Verifies a merchant's client credentials and issues a signed JWT (ADR-0010).
/// </summary>
public interface ITokenIssuanceService
{
    /// <summary>
    /// Looks up <paramref name="clientId"/>, verifies <paramref name="clientSecret"/> against the
    /// stored hash, and on success issues a signed access token whose <c>sub</c> is the merchant id.
    /// </summary>
    /// <returns>
    /// The issued token, or <c>null</c> if the client is unknown <b>or</b> the secret is wrong —
    /// the two failures are deliberately indistinguishable so callers can't probe which client ids
    /// exist.
    /// </returns>
    Task<IssuedToken?> IssueTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken = default);
}
