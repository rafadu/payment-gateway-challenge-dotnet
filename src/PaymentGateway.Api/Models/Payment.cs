namespace PaymentGateway.Api.Models;

/// <summary>
/// A persisted, bank-adjudicated payment. This is the gateway's internal domain model, kept
/// separate from the merchant-facing <see cref="Responses.PaymentResponse"/> DTO. Only the last
/// four digits of the card number are ever retained here — never the full PAN or CVV.
/// <see cref="Status"/> is only ever <see cref="PaymentStatus.Authorized"/> or
/// <see cref="PaymentStatus.Declined"/>; a rejected request is never persisted.
/// </summary>
public class Payment
{
    public Guid Id { get; init; }

    /// <summary>
    /// Identifies the merchant that owns this payment — set from the caller's JWT <c>sub</c> at
    /// creation time (ADR-0010). <see cref="PaymentsController"/> uses it to scope
    /// <c>GET /api/payments/{id}</c> so a merchant can only retrieve its own payments, returning
    /// <c>404</c> (not <c>403</c>) when the caller isn't the owner.
    /// </summary>
    public string MerchantId { get; init; } = string.Empty;

    public PaymentStatus Status { get; init; }

    /// <summary>Last four digits of the card number, kept as a string to preserve a leading zero.</summary>
    public string CardNumberLastFour { get; init; } = string.Empty;

    public int ExpiryMonth { get; init; }

    public int ExpiryYear { get; init; }

    public string Currency { get; init; } = string.Empty;

    public int Amount { get; init; }
}
