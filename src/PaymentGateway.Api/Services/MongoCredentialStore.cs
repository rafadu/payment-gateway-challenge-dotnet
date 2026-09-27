using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// MongoDB-backed <see cref="ICredentialStore"/> over the <c>merchants</c> collection seeded at
/// container startup (ADR-0010). This is the read-through source behind <see cref="CredentialCache"/>;
/// it is exercised by the integration suite and the manual docker check, not by unit tests (mocking
/// the Mongo driver would test the mock, not Mongo).
/// </summary>
public sealed class MongoCredentialStore : ICredentialStore
{
    private readonly IMongoCollection<MerchantDocument> _merchants;

    public MongoCredentialStore(IMongoDatabase database)
    {
        _merchants = database.GetCollection<MerchantDocument>("merchants");
    }

    public async Task<MerchantCredential?> FindByClientIdAsync(string clientId, CancellationToken cancellationToken = default)
    {
        var document = await _merchants
            .Find(m => m.ClientId == clientId)
            .FirstOrDefaultAsync(cancellationToken);

        return document is null
            ? null
            : new MerchantCredential(document.MerchantId, document.ClientId, document.HashedSecret);
    }

    /// <summary>Maps the <c>merchants</c> collection document. Extra fields (e.g. <c>createdAt</c>) are ignored.</summary>
    [BsonIgnoreExtraElements]
    internal sealed class MerchantDocument
    {
        [BsonId]
        public ObjectId Id { get; set; }

        [BsonElement("merchantId")]
        public string MerchantId { get; set; } = string.Empty;

        [BsonElement("clientId")]
        public string ClientId { get; set; } = string.Empty;

        [BsonElement("hashedSecret")]
        public string HashedSecret { get; set; } = string.Empty;
    }
}
