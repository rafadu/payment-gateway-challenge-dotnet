using Microsoft.Extensions.Caching.Memory;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Read-through cache of merchant credentials over <see cref="ICredentialStore"/> (ADR-0010),
/// backed by <see cref="IMemoryCache"/>. Entries use an <b>absolute</b> expiration set at write
/// time (not sliding), so a cached credential always falls back to the store at least once per TTL
/// — which is what lets a rotated/revoked secret eventually take effect. A not-found result is
/// never cached, so a newly seeded merchant becomes visible immediately.
///
/// <see cref="ICredentialCache"/> is the seam ADR-0011's Redis evolution would swap behind: the
/// same read-through/absolute-TTL contract over an <see cref="Microsoft.Extensions.Caching.Distributed.IDistributedCache"/>.
/// </summary>
public sealed class CredentialCache : ICredentialCache
{
    // The IMemoryCache is process-wide and could be shared with other features; namespace our keys
    // so a raw clientId can't collide with another cache user (also the shape the ADR-0011
    // IDistributedCache swap will want).
    private const string KeyPrefix = "merchant-cred:";

    private readonly ICredentialStore _store;
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _timeToLive;

    public CredentialCache(ICredentialStore store, IMemoryCache cache, TimeSpan timeToLive)
    {
        // A non-positive TTL (easy to mis-bind from config) would expire every entry on write and
        // silently disable caching — fail loudly at startup instead.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        _store = store;
        _cache = cache;
        _timeToLive = timeToLive;
    }

    public async Task<MerchantCredential?> GetByClientIdAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var cacheKey = KeyPrefix + clientId;

        if (_cache.TryGetValue(cacheKey, out MerchantCredential? cached))
        {
            return cached;
        }

        // No single-flight coalescing: concurrent misses for the same clientId may each hit the
        // store before the first Set lands. Acceptable at current scale (idempotent, tiny,
        // self-healing); the distributed evolution is deferred to ADR-0011.
        var credential = await _store.FindByClientIdAsync(clientId, cancellationToken);

        if (credential is null)
        {
            // Don't negatively cache — a re-seed/rotation should be picked up on the next request.
            return null;
        }

        _cache.Set(cacheKey, credential, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _timeToLive
        });

        return credential;
    }
}
