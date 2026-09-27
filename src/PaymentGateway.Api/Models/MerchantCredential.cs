namespace PaymentGateway.Api.Models;

/// <summary>
/// A merchant's authentication credential, as needed by the token-issuance flow. Only the
/// <b>hashed</b> secret is ever held — the plaintext secret exists only for the duration of a
/// single <c>/api/auth/token</c> request and is never stored or cached (ADR-0010). The Mongo
/// <c>createdAt</c> field is deliberately not carried here: the cache's TTL runs from cache-write
/// time, not from when the merchant was created.
/// </summary>
public sealed record MerchantCredential(string MerchantId, string ClientId, string HashedSecret);
