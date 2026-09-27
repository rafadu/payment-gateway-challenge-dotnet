namespace PaymentGateway.Api.Models.Responses;

/// <summary>
/// Merchant-facing payment representation, shared by the POST-success and GET responses
/// (the assessment's two response tables are identical). <see cref="Status"/> is only ever
/// <see cref="PaymentStatus.Authorized"/> or <see cref="PaymentStatus.Declined"/> — a rejected
/// request is never persisted and so is never returned in this shape.
/// </summary>
public class PaymentResponse
{
    public Guid Id { get; set; }

    public PaymentStatus Status { get; set; }

    /// <summary>Last four digits of the card number, kept as a string to preserve a leading zero.</summary>
    public string CardNumberLastFour { get; set; } = string.Empty;

    public int ExpiryMonth { get; set; }

    public int ExpiryYear { get; set; }

    public string Currency { get; set; } = string.Empty;

    public int Amount { get; set; }
}
