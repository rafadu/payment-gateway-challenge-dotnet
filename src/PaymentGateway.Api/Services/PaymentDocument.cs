using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// MongoDB document shape for the <c>payments</c> collection. Mirrors the <see cref="Payment"/>
/// domain model — only the fields the gateway needs at query time are persisted; CVV/PAN are
/// already absent at this layer (the domain model only carries <see cref="Payment.CardNumberLastFour"/>).
/// </summary>
internal sealed record PaymentDocument
{
    [BsonId]
    public string Id { get; init; } = string.Empty;

    [BsonElement("merchantId")]
    public string MerchantId { get; init; } = string.Empty;

    [BsonElement("status")]
    public string Status { get; init; } = string.Empty;

    [BsonElement("cardNumberLastFour")]
    public string CardNumberLastFour { get; init; } = string.Empty;

    [BsonElement("expiryMonth")]
    public int ExpiryMonth { get; init; }

    [BsonElement("expiryYear")]
    public int ExpiryYear { get; init; }

    [BsonElement("currency")]
    public string Currency { get; init; } = string.Empty;

    [BsonElement("amount")]
    public int Amount { get; init; }

    [BsonElement("createdAt")]
    public DateTime CreatedAt { get; init; }

    public static PaymentDocument FromDomain(Payment payment) => new()
    {
        Id = payment.Id.ToString(),
        MerchantId = payment.MerchantId,
        Status = payment.Status.ToString(),
        CardNumberLastFour = payment.CardNumberLastFour,
        ExpiryMonth = payment.ExpiryMonth,
        ExpiryYear = payment.ExpiryYear,
        Currency = payment.Currency,
        Amount = payment.Amount,
        CreatedAt = DateTime.UtcNow
    };

    public Payment ToDomain() => new()
    {
        Id = Guid.Parse(Id),
        MerchantId = MerchantId,
        Status = Enum.Parse<PaymentStatus>(Status),
        CardNumberLastFour = CardNumberLastFour,
        ExpiryMonth = ExpiryMonth,
        ExpiryYear = ExpiryYear,
        Currency = Currency,
        Amount = Amount
    };
}