using System.Text.Json.Serialization;

namespace PaymentGateway.Api.Models.Bank;

/// <summary>
/// The acquiring bank simulator's response wire contract (snake_case). <see cref="Authorized"/>
/// carries the bank's adjudication; <see cref="AuthorizationCode"/> is not part of the
/// merchant-facing contract and is not persisted (design.md).
/// </summary>
public sealed class BankPaymentResponse
{
    [JsonPropertyName("authorized")]
    public bool Authorized { get; init; }

    [JsonPropertyName("authorization_code")]
    public string? AuthorizationCode { get; init; }
}
