using MongoDB.Bson;
using MongoDB.Driver;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Persistence;

/// <summary>
/// MongoDB-backed <see cref="IAuditStore"/>. Writes one document per audit record to the
/// <c>audit_records</c> collection in the gateway database (same database as <c>merchants</c>,
/// per <see cref="MongoServiceCollectionExtensions"/>). Documents are BSON, queryable via
/// <c>mongosh</c> — e.g. <c>db.audit_records.find({ merchantId: "..." }).sort({ timestamp: -1 })</c>.
/// </summary>
public sealed class MongoAuditStore : IAuditStore
{
    private readonly IMongoCollection<BsonDocument> _collection;

    public MongoAuditStore(IMongoDatabase database)
    {
        _collection = database.GetCollection<BsonDocument>("audit_records");
    }

    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        var document = new BsonDocument
        {
            { "_id", record.Id.ToString() },
            { "timestamp", record.Timestamp },
            { "merchantId", record.MerchantId },
            { "method", record.Method },
            { "path", record.Path },
            { "statusCode", record.StatusCode },
            { "outcome", record.Outcome },
            { "durationMs", record.DurationMs },
            { "requestSummary", record.RequestSummary is null
                ? BsonNull.Value
                : new BsonDocument(record.RequestSummary
                    .Select(kv => new BsonElement(kv.Key, ToBsonValue(kv.Value)))) }
        };

        await _collection.InsertOneAsync(document, cancellationToken: cancellationToken);
    }

    private static BsonValue ToBsonValue(object? value) => value switch
    {
        null => BsonNull.Value,
        string s => s,
        int i => i,
        long l => l,
        double d => d,
        bool b => b,
        DateTime dt => dt,
        _ => BsonValue.Create(value)
    };
}