using MongoDB.Driver;

using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// MongoDB-backed <see cref="IPaymentsRepository"/>. Writes one document per payment to the
/// <c>payments</c> collection in the gateway database (same database as <c>merchants</c> and
/// <c>audit_records</c>, per <c>MongoServiceCollectionExtensions</c>). Documents are BSON,
/// queryable via <c>mongosh</c> — e.g. <c>db.payments.find({ merchantId: "..." }).sort({ _id: -1 })</c>.
/// Uses an upsert to honour the replace-on-duplicate contract that
/// <see cref="IPaymentsRepository.AddAsync"/> carries over from the original
/// <see cref="InMemoryPaymentsRepository"/>.
/// </summary>
public sealed class MongoPaymentsRepository : IPaymentsRepository
{
    private readonly IMongoCollection<PaymentDocument> _collection;

    public MongoPaymentsRepository(IMongoDatabase database)
    {
        _collection = database.GetCollection<PaymentDocument>("payments");
    }

    public async Task AddAsync(Payment payment, CancellationToken cancellationToken = default)
    {
        var document = PaymentDocument.FromDomain(payment);
        // Replace-on-duplicate (matches the original ConcurrentDictionary[indexer] semantics in
        // the in-memory implementation; in normal operation ids are GUIDs, so this rarely fires).
        await _collection.ReplaceOneAsync(
            Builders<PaymentDocument>.Filter.Eq(d => d.Id, document.Id),
            document,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken);
    }

    public async Task<Payment?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await _collection
            .Find(Builders<PaymentDocument>.Filter.Eq("_id", id.ToString()))
            .FirstOrDefaultAsync(cancellationToken);
        return document?.ToDomain();
    }
}