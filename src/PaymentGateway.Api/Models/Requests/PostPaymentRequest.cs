namespace PaymentGateway.Api.Models.Requests;

/// <summary>
/// Merchant-facing request to process a card payment. Field names and validation rules
/// map directly to the assessment's requirements table; the rules themselves live in
/// <see cref="Validation.PostPaymentRequestValidator"/>.
/// </summary>
public class PostPaymentRequest
{
    public string? CardNumber { get; set; }

    public int ExpiryMonth { get; set; }

    public int ExpiryYear { get; set; }

    public string? Currency { get; set; }

    /// <summary>Amount in the minor currency unit (e.g. cents). Must be a positive integer.</summary>
    public int Amount { get; set; }

    public string? Cvv { get; set; }
}
