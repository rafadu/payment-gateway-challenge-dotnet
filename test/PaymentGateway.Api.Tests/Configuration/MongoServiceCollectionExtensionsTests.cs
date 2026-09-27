using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using MongoDB.Driver;

using PaymentGateway.Api.Configuration;

namespace PaymentGateway.Api.Tests.Configuration;

public class MongoServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Registers_a_resolvable_mongo_client_and_database()
    {
        var services = new ServiceCollection();
        services.AddMongoDb(Config(
            ("Mongo:ConnectionString", "mongodb://localhost:27017"),
            ("Mongo:Database", "payment_gateway")));

        using var provider = services.BuildServiceProvider();

        // Both resolve without a running Mongo, because MongoClient/GetDatabase connect lazily.
        provider.GetService<IMongoClient>().Should().NotBeNull();
        provider.GetService<IMongoDatabase>().Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_when_the_connection_string_is_missing_or_blank(string? connectionString)
    {
        var config = Config(("Mongo:ConnectionString", connectionString), ("Mongo:Database", "db"));

        var act = () => new ServiceCollection().AddMongoDb(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mongo:ConnectionString*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_when_the_database_name_is_missing_or_blank(string? database)
    {
        var config = Config(("Mongo:ConnectionString", "mongodb://localhost:27017"), ("Mongo:Database", database));

        var act = () => new ServiceCollection().AddMongoDb(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Mongo:Database*");
    }
}
