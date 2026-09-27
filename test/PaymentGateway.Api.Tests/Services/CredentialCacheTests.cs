using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;

using NSubstitute;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

public class CredentialCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(4);

    private readonly ICredentialStore _store = Substitute.For<ICredentialStore>();
    private readonly MemoryCache _memoryCache = new(new MemoryCacheOptions());
    private readonly CredentialCache _cache;

    public CredentialCacheTests()
    {
        _cache = new CredentialCache(_store, _memoryCache, Ttl);
    }

    private static MerchantCredential Cred(string clientId = "client-1", string hashedSecret = "hashed") =>
        new("merchant-1", clientId, hashedSecret);

    private void StoreHas(MerchantCredential credential) =>
        _store.FindByClientIdAsync(credential.ClientId, Arg.Any<CancellationToken>()).Returns(credential);

    [Fact]
    public async Task Fetches_from_the_store_on_a_miss()
    {
        StoreHas(Cred());

        var result = await _cache.GetByClientIdAsync("client-1");

        result.Should().Be(Cred());
        await _store.Received(1).FindByClientIdAsync("client-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Serves_a_cached_entry_without_re_querying_the_store()
    {
        StoreHas(Cred());

        await _cache.GetByClientIdAsync("client-1"); // populates the cache
        var result = await _cache.GetByClientIdAsync("client-1");

        result.Should().Be(Cred());
        await _store.Received(1).FindByClientIdAsync("client-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Returns_null_when_the_store_has_no_such_client()
    {
        _store.FindByClientIdAsync("unknown", Arg.Any<CancellationToken>())
            .Returns((MerchantCredential?)null);

        var result = await _cache.GetByClientIdAsync("unknown");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Does_not_cache_a_not_found_result()
    {
        _store.FindByClientIdAsync("unknown", Arg.Any<CancellationToken>())
            .Returns((MerchantCredential?)null);

        await _cache.GetByClientIdAsync("unknown");
        await _cache.GetByClientIdAsync("unknown");

        await _store.Received(2).FindByClientIdAsync("unknown", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Re_fetches_and_serves_the_new_value_after_the_entry_is_evicted()
    {
        // Rationale from ADR-0010: expiry (here simulated by eviction) makes the cache fall back to
        // the store, so a rotated secret is eventually served.
        _store.FindByClientIdAsync("client-1", Arg.Any<CancellationToken>())
            .Returns(Cred(hashedSecret: "old-hash"), Cred(hashedSecret: "new-hash"));

        var first = await _cache.GetByClientIdAsync("client-1");
        _memoryCache.Clear(); // stand-in for the absolute-TTL eviction (key-format agnostic)
        var second = await _cache.GetByClientIdAsync("client-1");

        first!.HashedSecret.Should().Be("old-hash");
        second!.HashedSecret.Should().Be("new-hash");
        await _store.Received(2).FindByClientIdAsync("client-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caches_the_credential_with_the_configured_absolute_ttl()
    {
        // The actual time-based eviction is IMemoryCache's (framework) responsibility; what this
        // cache owns is configuring the correct ABSOLUTE expiration, so that is what we verify.
        var mockCache = Substitute.For<IMemoryCache>();
        var entry = Substitute.For<ICacheEntry>();
        mockCache.CreateEntry(Arg.Any<object>()).Returns(entry);
        var cache = new CredentialCache(_store, mockCache, Ttl);
        StoreHas(Cred());

        await cache.GetByClientIdAsync("client-1");

        // Absolute (relative-to-now) expiry, not sliding — a cached entry can't be kept alive
        // indefinitely by sustained reads (ADR-0010).
        entry.Received().AbsoluteExpirationRelativeToNow = Ttl;
        entry.Received().SlidingExpiration = null;
    }

    [Fact]
    public async Task Propagates_the_cancellation_token_to_the_store()
    {
        using var cts = new CancellationTokenSource();
        StoreHas(Cred());

        await _cache.GetByClientIdAsync("client-1", cts.Token);

        await _store.Received(1).FindByClientIdAsync("client-1", cts.Token);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_a_non_positive_ttl(int ttlHours)
    {
        var act = () => new CredentialCache(_store, _memoryCache, TimeSpan.FromHours(ttlHours));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
