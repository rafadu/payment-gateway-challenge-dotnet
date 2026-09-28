using PaymentGateway.Api.Models.Bank;
using PaymentGateway.Api.Models.Requests;

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

    /// <summary>
    /// Builds a <see cref="Payment"/> from a merchant-validated <paramref name="request"/> and the
    /// bank's adjudication. The factory generates a fresh id (unless <paramref name="id"/> is
    /// supplied), keeps only the last four digits of the card number, and stamps the payment with
    /// the caller's <paramref name="merchantId"/>. The <paramref name="request"/> is assumed
    /// already validated: <c>CardNumber</c>/<c>Currency</c>/<c>Cvv</c> are non-null and the card
    /// number is at least four digits long.
    /// </summary>
    /// <param name="id">
    /// Optional pre-generated id. The outbox (§3.2 of
    /// <c>docs/post-payment-orchestration-improvements.md</c>) generates the id upfront and shares
    /// it between the <c>BankIntent</c> and the eventual <c>Payment</c>, so a sweeper-materialized
    /// Payment has the same id the merchant's cached Idempotency-Key response would have used.
    /// Pass <c>null</c> (the default) to let the factory generate a new id.
    /// </param>
    public static Payment FromBankOutcome(
        string merchantId,
        PostPaymentRequest request,
        BankPaymentResponse bankResponse,
        Guid? id = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            MerchantId = merchantId,
            Status = bankResponse.Authorized ? PaymentStatus.Authorized : PaymentStatus.Declined,
            CardNumberLastFour = request.CardNumber![^4..],
            ExpiryMonth = request.ExpiryMonth,
            ExpiryYear = request.ExpiryYear,
            Currency = request.Currency!,
            Amount = request.Amount
        };
}
