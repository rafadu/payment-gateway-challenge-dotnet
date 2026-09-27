using System.Text.Json.Serialization;

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
}
