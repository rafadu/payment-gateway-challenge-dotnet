using MongoDB.Driver;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the MongoDB infrastructure (client + database) from the <c>Mongo</c> configuration
/// section. Kept out of <c>Program.cs</c> so the composition root stays a thin list of features.
/// </summary>
public static class MongoServiceCollectionExtensions
{
    public static IServiceCollection AddMongoDb(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetValue<string>("Mongo:ConnectionString");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Missing configuration: Mongo:ConnectionString.");
        }

        var databaseName = configuration.GetValue<string>("Mongo:Database");
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new InvalidOperationException("Missing configuration: Mongo:Database.");
        }

        // MongoClient is thread-safe and owns the connection pool, so it is a singleton; it connects
        // lazily, so the app still starts with Mongo down.
        services.AddSingleton<IMongoClient>(_ => new MongoClient(connectionString));
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(databaseName));

        return services;
    }
}
