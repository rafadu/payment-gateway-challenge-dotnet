using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Models;

/// <summary>
/// Sanitized snapshot of a merchant <see cref="PostPaymentRequest"/> for storage on a
/// <see cref="BankIntent"/> outbox record. Holds only what the reconciler needs to materialize a
/// <see cref="Payment"/> if the gateway crashed mid-flow: last four of the card, expiry,
/// currency, and amount. Never the full PAN or CVV — the ADR-0008 architecture-test PCI safety
/// net forbids those names on any type other than <see cref="PostPaymentRequest"/> and the bank
/// wire DTOs.
/// </summary>
public class BankIntentRequest
{
    public string CardLastFour { get; init; } = string.Empty;
    public int ExpiryMonth { get; init; }
    public int ExpiryYear { get; init; }
    public string Currency { get; init; } = string.Empty;
    public int Amount { get; init; }

    /// <summary>
    /// Builds the snapshot from a merchant-validated <paramref name="request"/>. Extracts only
    /// the last four digits of the card number; the full PAN and CVV never enter the intent.
    /// Assumes the request is already validated (14–19 digit card number, etc.).
    /// </summary>
    public static BankIntentRequest FromMerchantRequest(PostPaymentRequest request) =>
        new()
        {
            CardLastFour = request.CardNumber![^4..],
            ExpiryMonth = request.ExpiryMonth,
            ExpiryYear = request.ExpiryYear,
            Currency = request.Currency!,
            Amount = request.Amount,
        };
}
