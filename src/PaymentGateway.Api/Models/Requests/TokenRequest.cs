namespace PaymentGateway.Api.Models.Requests;

/// <summary>Merchant login request for <c>POST /api/auth/token</c> (ADR-0010).</summary>
public sealed class TokenRequest
{
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }
}
