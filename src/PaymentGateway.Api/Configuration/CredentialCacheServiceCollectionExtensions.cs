using Microsoft.Extensions.Caching.Memory;

using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the MongoDB-backed merchant credential store behind the in-memory, read-through
/// credential cache (ADR-0010), with the absolute TTL taken from the <c>CredentialCache</c>
/// configuration section. Requires <see cref="MongoServiceCollectionExtensions.AddMongoDb"/> to
/// have registered the <see cref="MongoDB.Driver.IMongoDatabase"/> the store depends on.
/// </summary>
public static class CredentialCacheServiceCollectionExtensions
{
    private const double DefaultTtlHours = 4d;

    public static IServiceCollection AddCredentialCache(this IServiceCollection services, IConfiguration configuration)
    {
        var ttlHours = configuration.GetValue("CredentialCache:TtlHours", DefaultTtlHours);
        if (ttlHours <= 0)
        {
            throw new InvalidOperationException("Configuration CredentialCache:TtlHours must be greater than zero.");
        }

        services.AddSingleton<ICredentialStore, MongoCredentialStore>();
        services.AddMemoryCache();
        services.AddSingleton<ICredentialCache>(sp => new CredentialCache(
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<IMemoryCache>(),
            TimeSpan.FromHours(ttlHours)));

        return services;
    }
}
