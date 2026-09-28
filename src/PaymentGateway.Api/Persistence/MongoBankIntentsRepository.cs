using Microsoft.Extensions.Logging;

using MongoDB.Driver;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Persistence;

/// <summary>
/// MongoDB-backed <see cref="IBankIntentsRepository"/> for the outbox (§3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>, ADR-0013). Singleton — one collection
/// per process; the driver owns the connection pool.
///
/// <para>The constructor creates the <c>(Status, UpdatedAt)</c> index used by the reconciler's
/// <see cref="FindStaleAsync"/>. Best-effort: a failure is logged but not thrown, so the app
/// still starts when Mongo is briefly unreachable. Without the index, <see cref="FindStaleAsync"/>
/// becomes a full collection scan — slower but still correct.</para>
/// </summary>
public sealed class MongoBankIntentsRepository : IBankIntentsRepository
{
    private readonly IMongoCollection<BankIntentDocument> _collection;
    private readonly ILogger<MongoBankIntentsRepository> _logger;

    public MongoBankIntentsRepository(IMongoDatabase database, ILogger<MongoBankIntentsRepository> logger)
    {
        _logger = logger;
        _collection = database.GetCollection<BankIntentDocument>("bank_intents");
        EnsureIndexes();
    }

    public async Task AddAsync(BankIntent intent, CancellationToken cancellationToken = default)
    {
        // InsertOne throws on duplicate _id, which matches the in-memory repo's TryAdd-throws
        // contract. The handler always generates a fresh id, so this is an unexpected situation
        // worth surfacing — a previous attempt at the same payment already wrote an intent.
        var document = BankIntentDocument.FromDomain(intent);
        await _collection.InsertOneAsync(document, cancellationToken: cancellationToken);
    }

    public async Task<BankIntent?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await _collection
            .Find(d => d.Id == id.ToString())
            .FirstOrDefaultAsync(cancellationToken);
        return document?.ToDomain();
    }

    public async Task RecordOutcomeAsync(
        Guid id,
        BankPaymentResponse response,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var newStatus = response.Authorized ? BankIntentStatus.Authorized : BankIntentStatus.Declined;
        var update = Builders<BankIntentDocument>.Update
            .Set(d => d.Response, response)
            .Set(d => d.Status, newStatus)
            .Set(d => d.UpdatedAt, updatedAt);
        await _collection.UpdateOneAsync(
            Builders<BankIntentDocument>.Filter.Eq(d => d.Id, id.ToString()),
            update,
            cancellationToken: cancellationToken);
    }

    public async Task MarkReconciledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        var update = Builders<BankIntentDocument>.Update
            .Set(d => d.Status, BankIntentStatus.Reconciled)
            .Set(d => d.UpdatedAt, updatedAt);
        await _collection.UpdateOneAsync(
            Builders<BankIntentDocument>.Filter.Eq(d => d.Id, id.ToString()),
            update,
            cancellationToken: cancellationToken);
    }

    public async Task MarkCancelledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        var update = Builders<BankIntentDocument>.Update
            .Set(d => d.Status, BankIntentStatus.Cancelled)
            .Set(d => d.UpdatedAt, updatedAt);
        await _collection.UpdateOneAsync(
            Builders<BankIntentDocument>.Filter.Eq(d => d.Id, id.ToString()),
            update,
            cancellationToken: cancellationToken);
    }

    public async Task IncrementAttemptsAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        var update = Builders<BankIntentDocument>.Update
            .Inc(d => d.Attempts, 1)
            .Set(d => d.UpdatedAt, updatedAt);
        await _collection.UpdateOneAsync(
            Builders<BankIntentDocument>.Filter.Eq(d => d.Id, id.ToString()),
            update,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<BankIntent>> FindStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        // Stale-detection uses real UtcNow (per the codebase convention: production code uses real
        // time, test fakes use TimeProvider). The stale threshold is parameterised; tests that
        // want deterministic timing use a small value rather than driving a clock.
        var cutoff = DateTime.UtcNow - staleAfter;

        var filter = Builders<BankIntentDocument>.Filter.And(
            Builders<BankIntentDocument>.Filter.In(d => d.Status, new[]
            {
                BankIntentStatus.Pending,
                BankIntentStatus.Authorized,
                BankIntentStatus.Declined
            }),
            Builders<BankIntentDocument>.Filter.Lt(d => d.UpdatedAt, cutoff));

        var documents = await _collection.Find(filter).ToListAsync(cancellationToken);
        return documents.Select(d => d.ToDomain()).ToList();
    }

    private void EnsureIndexes()
    {
        try
        {
            var keys = Builders<BankIntentDocument>.IndexKeys
                .Ascending(d => d.Status)
                .Ascending(d => d.UpdatedAt);
            var indexModel = new CreateIndexModel<BankIntentDocument>(keys);
            _collection.Indexes.CreateOne(indexModel);
        }
        catch (Exception ex)
        {
            // Best-effort: the index is critical for FindStaleAsync performance (without it,
            // every poll is a full collection scan), but a missing index is a soft failure —
            // the repo still works, just slower. Logged so ops can spot and fix.
            _logger.LogWarning(ex,
                "Failed to ensure (Status, UpdatedAt) index on bank_intents collection; FindStaleAsync will do a full collection scan until the index exists.");
        }
    }
}
