using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers JWT Bearer authentication backed by the same HMAC-SHA256 signing key used for token
/// issuance (ADR-0010). Validation parameters pin the allowed algorithm to <c>HS256</c> — preventing
/// <c>alg=none</c>/algorithm-confusion attacks — and disable issuer/audience checks because the
/// token format intentionally omits them (ADR-0010). Inbound claim mapping is disabled so the
/// issued <c>sub</c> reads back as <c>sub</c> on the authenticated principal.
/// </summary>
public static class JwtAuthenticationServiceCollectionExtensions
{
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var signingKey = configuration.GetValue<string>("Jwt:SigningKey");
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("Missing configuration: Jwt:SigningKey.");
        }

        // Fail fast at startup on a too-short key rather than on the first authenticated request.
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new InvalidOperationException(
                "Configuration Jwt:SigningKey must be at least 256 bits (32 bytes) for HMAC-SHA256.");
        }

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = false,
                    ValidateAudience = false,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey))
                };
            });

        return services;
    }
}