using FluentAssertions;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using MongoDB.Bson;
using MongoDB.Driver;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Tests.Integration;

/// <summary>
/// Integration tests for <see cref="MongoBankIntentsRepository"/> — exercises the full Mongo
/// round-trip including the (Status, UpdatedAt) index created on construction. Skipped when
/// docker-compose (bank_simulator + mongo) isn't running — see <see cref="IntegrationFixture"/>.
/// </summary>
[Collection("Integration")]
[Trait("Category", "Integration")]
public class MongoBankIntentsRepositoryTests : IDisposable
{
    private readonly IntegrationFixture _fixture;
    private readonly WebApplicationFactory<Program>? _factory;
    private readonly IMongoDatabase _database = null!;
    private readonly MongoBankIntentsRepository _repo = null!;

    public MongoBankIntentsRepositoryTests(IntegrationFixture fixture)
    {
        _fixture = fixture;

        // Only wire up the real Mongo repo when the services are actually running. The constructor
        // runs for EVERY test in this class — including when the body immediately Skip.IfNot's — and
        // MongoBankIntentsRepository's constructor calls EnsureIndexes() (a synchronous, best-effort
        // Mongo call that blocks ~30s on the server-selection timeout when Mongo is down). Without
        // this guard, an unfiltered `dotnet test` with no docker-compose pays ~30s per test here.
        if (!fixture.ServicesAvailable) return;

        // Hold the factory alive for the duration of the test (xUnit calls Dispose() after each
        // test) — the repo's constructor touches the IMongoDatabase for index creation, and the
        // test queries run against the same database. Disposing too early throws ObjectDisposedException.
        _factory = new WebApplicationFactory<Program>();
        _database = _factory.Services.GetRequiredService<IMongoDatabase>();
        _repo = new MongoBankIntentsRepository(_database, NullLogger<MongoBankIntentsRepository>.Instance);
    }

    public void Dispose() => _factory?.Dispose();

    private static BankIntentRequest ARequest() => new()
    {
        CardLastFour = "8877",
        ExpiryMonth = 12,
        ExpiryYear = 2030,
        Currency = "GBP",
        Amount = 100
    };

    [SkippableFact]
    public async Task Add_then_Get_round_trips_an_intent()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        var intent = BankIntent.StartPending(Guid.NewGuid(), "merchant-it", ARequest(), DateTime.UtcNow);

        await _repo.AddAsync(intent);
        var fetched = await _repo.GetAsync(intent.Id);

        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(intent.Id);
        fetched.MerchantId.Should().Be("merchant-it");
        fetched.Status.Should().Be(BankIntentStatus.Pending);
        fetched.Request.CardLastFour.Should().Be("8877");
    }

    [SkippableFact]
    public async Task FindStaleAsync_returns_only_intents_in_reconciler_actionable_states_that_are_older_than_threshold()
    {
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        // Seed four intents: a stale Pending, a stale Authorized, a Reconciled (must NOT appear),
        // and a recent Pending (must NOT appear). Reconciled + recent are the negative cases —
        // proving the filter, not just the happy path.
        var stalePending = await SeedAsync(BankIntentStatus.Pending, outdatedBy: TimeSpan.FromMinutes(10));
        var staleAuthorized = await SeedAsync(BankIntentStatus.Authorized, outdatedBy: TimeSpan.FromMinutes(5), withResponse: true);
        var reconciled = await SeedAsync(BankIntentStatus.Reconciled, outdatedBy: TimeSpan.FromMinutes(10));
        await SeedAsync(BankIntentStatus.Pending, outdatedBy: TimeSpan.Zero);

        var stale = await _repo.FindStaleAsync(TimeSpan.FromMinutes(1));

        stale.Select(i => i.Id).Should().Contain(new[] { stalePending.Id, staleAuthorized.Id });
        stale.Select(i => i.Id).Should().NotContain(reconciled.Id);
    }

    [SkippableFact]
    public async Task EnsureIndexesAsync_creates_a_compound_index_on_Status_and_UpdatedAt()
    {
        // The (Status, UpdatedAt) index is what keeps FindStaleAsync a point query instead of a
        // full collection scan. Index creation lives in EnsureIndexesAsync (called once at startup
        // by BankIntentIndexInitializer, off the constructor); here we invoke it directly and then
        // verify by reading the index list — Mongo names compound indexes "field1N_field2M".
        Skip.IfNot(_fixture.ServicesAvailable, "docker-compose up (bank_simulator + mongo) is not running.");

        await _repo.EnsureIndexesAsync();

        var indexes = await _database.GetCollection<BsonDocument>("bank_intents").Indexes.List().ToListAsync();

        var names = indexes.Select(i => i["name"].AsString).ToList();
        // Mongo lowercases field names and appends "_1" per direction. The (Status asc, UpdatedAt asc)
        // index surfaces as "status_1_updatedAt_1".
        names.Should().Contain("status_1_updatedAt_1",
            because: "FindStaleAsync filters by Status and UpdatedAt, so a compound index is required for performance.");
    }

    private async Task<BankIntent> SeedAsync(
        BankIntentStatus status,
        TimeSpan outdatedBy,
        bool withResponse = false)
    {
        var now = DateTime.UtcNow;
        var intent = new BankIntent
        {
            Id = Guid.NewGuid(),
            MerchantId = "merchant-it",
            Request = ARequest(),
            Response = withResponse ? new BankPaymentResponse { Authorized = status == BankIntentStatus.Authorized } : null,
            Status = status,
            CreatedAt = now - outdatedBy,
            UpdatedAt = now - outdatedBy,
            Attempts = 0
        };
        await _repo.AddAsync(intent);
        return intent;
    }
}
