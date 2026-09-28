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
/// <para>The <c>(Status, UpdatedAt)</c> index used by the reconciler's <see cref="FindStaleAsync"/>
/// is created by <see cref="EnsureIndexesAsync"/>, invoked once at startup by
/// <see cref="Services.BankIntentIndexInitializer"/> — deliberately off the constructor so
/// construction never blocks on network I/O. Best-effort: a failure is logged but not thrown, so
/// the app still starts when Mongo is briefly unreachable. Without the index,
/// <see cref="FindStaleAsync"/> becomes a full collection scan — slower but still correct.</para>
/// </summary>
public sealed class MongoBankIntentsRepository : IBankIntentsRepository
{
    private readonly IMongoCollection<BankIntentDocument> _collection;
    private readonly ILogger<MongoBankIntentsRepository> _logger;

    public MongoBankIntentsRepository(IMongoDatabase database, ILogger<MongoBankIntentsRepository> logger)
    {
        _logger = logger;
        _collection = database.GetCollection<BankIntentDocument>("bank_intents");
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

    public async Task<int> DeleteReconciledOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        // Compound filter matches the existing (Status, UpdatedAt) index, so the delete is a
        // bounded index scan rather than a collection scan. Only Reconciled intents with a
        // stale UpdatedAt are removed — Pending/Authorized/Declined/Cancelled are deliberately
        // preserved (the cleanup pass is purely about bounding the collection's growth).
        var filter = Builders<BankIntentDocument>.Filter.And(
            Builders<BankIntentDocument>.Filter.Eq(d => d.Status, BankIntentStatus.Reconciled),
            Builders<BankIntentDocument>.Filter.Lt(d => d.UpdatedAt, olderThan));

        var result = await _collection.DeleteManyAsync(filter, cancellationToken);
        return (int)result.DeletedCount;
    }

    public async Task<int> DeletePendingOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        // Pending uses CreatedAt (not UpdatedAt): the sweeper's IncrementAttemptsAsync refreshes
        // UpdatedAt on every pass, so a cutoff on UpdatedAt would keep Pending ineligible
        // forever. CreatedAt is set at intent creation and never mutated, so it's the true age.
        // (Status, UpdatedAt) is the existing compound index — this filter doesn't use it because
        // Pending is rare and a full collection scan of "Pending" via the Status filter alone
        // is acceptable for a daily cleanup pass.
        var filter = Builders<BankIntentDocument>.Filter.And(
            Builders<BankIntentDocument>.Filter.Eq(d => d.Status, BankIntentStatus.Pending),
            Builders<BankIntentDocument>.Filter.Lt(d => d.CreatedAt, olderThan));

        var result = await _collection.DeleteManyAsync(filter, cancellationToken);
        return (int)result.DeletedCount;
    }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var keys = Builders<BankIntentDocument>.IndexKeys
                .Ascending(d => d.Status)
                .Ascending(d => d.UpdatedAt);
            var indexModel = new CreateIndexModel<BankIntentDocument>(keys);
            await _collection.Indexes.CreateOneAsync(indexModel, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host is shutting down mid-creation — propagate so the caller can stop cleanly rather
            // than log this as a failure. The index will be re-attempted on the next start.
            throw;
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
