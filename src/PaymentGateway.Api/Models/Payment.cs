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

    public PaymentStatus Status { get; init; }

    /// <summary>Last four digits of the card number, kept as a string to preserve a leading zero.</summary>
    public string CardNumberLastFour { get; init; } = string.Empty;

    public int ExpiryMonth { get; init; }

    public int ExpiryYear { get; init; }

    public string Currency { get; init; } = string.Empty;

    public int Amount { get; init; }
}
