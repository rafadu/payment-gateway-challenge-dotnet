using FluentAssertions;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Configuration;

public class CredentialCacheServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    // MongoCredentialStore depends on IMongoDatabase. Register the real (lazy) Mongo services so the
    // store constructs without a running server — GetCollection does no I/O.
    private static ServiceCollection ServicesWithMongo()
    {
        var services = new ServiceCollection();
        services.AddMongoDb(Config(
            ("Mongo:ConnectionString", "mongodb://localhost:27017"),
            ("Mongo:Database", "payment_gateway")));
        return services;
    }

    [Fact]
    public void Registers_a_resolvable_store_memory_cache_and_credential_cache()
    {
        var services = ServicesWithMongo();
        services.AddCredentialCache(Config(("CredentialCache:TtlHours", "4")));

        using var provider = services.BuildServiceProvider();

        provider.GetService<ICredentialStore>().Should().BeOfType<MongoCredentialStore>();
        provider.GetService<IMemoryCache>().Should().NotBeNull();
        provider.GetService<ICredentialCache>().Should().BeOfType<CredentialCache>();
    }

    [Fact]
    public void Uses_the_default_ttl_when_not_configured()
    {
        var services = ServicesWithMongo();
        services.AddCredentialCache(Config()); // no CredentialCache:TtlHours

        using var provider = services.BuildServiceProvider();

        // Resolving proves the default TTL is positive (a non-positive TTL would throw in the ctor).
        provider.GetService<ICredentialCache>().Should().NotBeNull();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Throws_when_ttl_hours_is_not_positive(string ttlHours)
    {
        var act = () => new ServiceCollection().AddCredentialCache(Config(("CredentialCache:TtlHours", ttlHours)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*TtlHours*");
    }
}
