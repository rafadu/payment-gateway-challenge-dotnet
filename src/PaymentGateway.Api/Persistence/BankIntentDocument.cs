using MongoDB.Bson.Serialization.Attributes;

using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Persistence;

/// <summary>
/// BSON-mapped view of <see cref="BankIntent"/> for the <c>bank_intents</c> Mongo collection.
/// Internal to the persistence layer; the domain model (<see cref="BankIntent"/>) is what flows
/// through the rest of the codebase.
///
/// <para><see cref="Id"/> is stored as a string (not a <see cref="Guid"/>) to match the
/// existing pattern in <see cref="PaymentDocument"/> and sidestep the
/// <c>GuidRepresentation.Unspecified</c> serialization issue — MongoDB.Driver requires either a
/// registered serializer for <see cref="Guid"/> or an explicit representation on every field
/// otherwise.</para>
/// </summary>
internal class BankIntentDocument
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonElement("merchantId")]
    public string MerchantId { get; set; } = string.Empty;

    [BsonElement("request")]
    public BankIntentRequest Request { get; set; } = null!;

    [BsonElement("response")]
    [BsonIgnoreIfNull]
    public BankPaymentResponse? Response { get; set; }

    [BsonElement("status")]
    [BsonRepresentation(MongoDB.Bson.BsonType.String)]
    public BankIntentStatus Status { get; set; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; set; }

    [BsonElement("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [BsonElement("attempts")]
    public int Attempts { get; set; }

    public static BankIntentDocument FromDomain(BankIntent intent) => new()
    {
        Id = intent.Id.ToString(),
        MerchantId = intent.MerchantId,
        Request = intent.Request,
        Response = intent.Response,
        Status = intent.Status,
        CreatedAt = intent.CreatedAt,
        UpdatedAt = intent.UpdatedAt,
        Attempts = intent.Attempts
    };

    public BankIntent ToDomain() => new()
    {
        Id = Guid.Parse(Id),
        MerchantId = MerchantId,
        Request = Request,
        Response = Response,
        Status = Status,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Attempts = Attempts
    };
}
