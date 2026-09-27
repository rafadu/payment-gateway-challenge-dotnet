using System.Text;

using Microsoft.Extensions.DependencyInjection.Extensions;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers JWT token issuance from the <c>Jwt</c> configuration section (ADR-0010). Depends on
/// <see cref="ICredentialCache"/> (registered by <c>AddCredentialCache</c>) for the credential lookup.
/// </summary>
public static class TokenIssuanceServiceCollectionExtensions
{
    private const double DefaultExpiryMinutes = 15d;

    public static IServiceCollection AddTokenIssuance(this IServiceCollection services, IConfiguration configuration)
    {
        var signingKey = configuration.GetValue<string>("Jwt:SigningKey");
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("Missing configuration: Jwt:SigningKey.");
        }

        // Fail fast at startup on a too-short key rather than on the first /api/auth/token call.
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "Configuration Jwt:SigningKey must be at least 256 bits (32 bytes) for HMAC-SHA256.");
        }

        var expiryMinutes = configuration.GetValue("Jwt:ExpiryMinutes", DefaultExpiryMinutes);
        if (expiryMinutes <= 0)
        {
            throw new InvalidOperationException("Configuration Jwt:ExpiryMinutes must be greater than zero.");
        }

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ITokenIssuanceService>(sp => new TokenIssuanceService(
            sp.GetRequiredService<ICredentialCache>(),
            sp.GetRequiredService<TimeProvider>(),
            signingKey,
            TimeSpan.FromMinutes(expiryMinutes)));

        return services;
    }
}
