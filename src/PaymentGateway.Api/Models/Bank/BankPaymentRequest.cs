using System.Text.Json.Serialization;

using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Models.Bank;

/// <summary>
/// The acquiring bank simulator's request wire contract (snake_case). Kept separate from the
/// merchant-facing <see cref="Requests.PostPaymentRequest"/> so the two JSON conventions never
/// leak into each other. <see cref="ExpiryDate"/> is the bank's <c>"MM/yyyy"</c> format.
/// </summary>
public sealed class BankPaymentRequest
{
    [JsonPropertyName("card_number")]
    public string CardNumber { get; init; } = string.Empty;

    [JsonPropertyName("expiry_date")]
    public string ExpiryDate { get; init; } = string.Empty;

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = string.Empty;

    [JsonPropertyName("amount")]
    public int Amount { get; init; }

    [JsonPropertyName("cvv")]
    public string Cvv { get; init; } = string.Empty;

    /// <summary>
    /// Builds the bank's wire-format request from a merchant-validated
    /// <paramref name="request"/>. Sends the full PAN and CVV (the bank needs them); the gateway
    /// keeps only the last four. <paramref name="request"/> is assumed already validated: every
    /// string field is non-null.
    /// </summary>
    public static BankPaymentRequest FromMerchantRequest(PostPaymentRequest request) => new()
    {
        CardNumber = request.CardNumber!,
        ExpiryDate = $"{request.ExpiryMonth:D2}/{request.ExpiryYear:D4}",
        Currency = request.Currency!,
        Amount = request.Amount,
        Cvv = request.Cvv!
    };
}
