using System.Net.Http;
using System.Net.Sockets;

using MongoDB.Bson;
using MongoDB.Driver;

namespace PaymentGateway.Api.Tests.Integration;

/// <summary>
/// Collection fixture for the integration tests: probes the real bank simulator
/// (<c>localhost:8080</c>) and MongoDB (<c>localhost:27017</c>) on first use and exposes a
/// <see cref="ServicesAvailable"/> flag the tests use to skip themselves. The skip path is
/// intentional — running <c>dotnet test</c> in an environment without <c>docker-compose up</c>
/// must produce skipped (not failed) tests, with a clear message telling the user how to run
/// the integration suite: <c>dotnet test --filter "Category=Integration"</c>.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    /// <summary>True when both the bank simulator and MongoDB answered during the probe.</summary>
    public bool ServicesAvailable { get; private set; }

    public async Task InitializeAsync()
    {
        ServicesAvailable = await ProbeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<bool> ProbeAsync()
    {
        if (!await CanConnectTcpAsync("localhost", 8080)) return false;
        if (!await CanConnectTcpAsync("localhost", 27017)) return false;

        // Bank simulator responds to HTTP even when its imposters aren't loaded yet — Mountebank
        // returns 400 (the configured `defaultResponse` in `bank_simulator.ejs:6-15`) from `/`,
        // which is fine: we just need to know it answers.
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            await http.GetAsync("http://localhost:8080/");
        }
        catch
        {
            return false;
        }

        // Confirm Mongo serves the seed-populated `payment_gateway` database AND that the
        // `merchants` collection contains the demo merchant seeded by mongo-init. A bare `ping`
        // would pass against an unseeded `/data/db` volume, leaving the auth-flow tests to fail
        // confusingly later.
        try
        {
            var client = new MongoClient("mongodb://localhost:27017");
            var merchants = client.GetDatabase("payment_gateway")
                .GetCollection<BsonDocument>("merchants");
            var demoMerchant = await merchants
                .Find(Builders<BsonDocument>.Filter.Eq("clientId", "demo-merchant"))
                .FirstOrDefaultAsync();
            if (demoMerchant is null) return false;
        }
        catch
        {
            return false;
        }

        return true;
    }

    private static async Task<bool> CanConnectTcpAsync(string host, int port)
    {
        using var client = new TcpClient { ReceiveTimeout = 2000, SendTimeout = 2000 };
        try
        {
            await client.ConnectAsync(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition("Integration")]
public sealed class IntegrationCollection : ICollectionFixture<IntegrationFixture> { }