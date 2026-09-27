namespace PaymentGateway.Api.Models.Responses;

/// <summary>Access token returned by <c>POST /api/auth/token</c>. Used as <c>Authorization: Bearer &lt;accessToken&gt;</c>.</summary>
public sealed class TokenResponse
{
    public string AccessToken { get; init; } = string.Empty;

    public string TokenType { get; init; } = "Bearer";

    /// <summary>Token lifetime in seconds.</summary>
    public int ExpiresIn { get; init; }
}
