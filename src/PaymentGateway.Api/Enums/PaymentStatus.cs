using System.Text.Json.Serialization;

namespace PaymentGateway.Api.Models;

/// <summary>
/// Lifecycle status of a persisted <c>Payment</c>. The merchant-facing response body carries
/// <see cref="System.Text.Json.Serialization.JsonStringEnumConverter"/> on this enum so the
/// status appears as <c>"Authorized"</c> / <c>"Declined"</c> in JSON rather than the integer
/// ordinal — the HTTP response is the public contract, and a stable string name is
/// self-documenting and reorder-safe.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentStatus
{
    Authorized,
    Declined
}
