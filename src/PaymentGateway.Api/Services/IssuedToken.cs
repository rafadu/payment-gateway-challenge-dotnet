namespace PaymentGateway.Api.Services;

/// <summary>A successfully issued access token and how long (in seconds) it remains valid.</summary>
public sealed record IssuedToken(string AccessToken, int ExpiresInSeconds);
