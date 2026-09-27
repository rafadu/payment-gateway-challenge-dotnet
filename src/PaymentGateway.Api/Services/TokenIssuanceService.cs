using System.Text;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Issues HMAC-SHA256-signed JWTs after verifying a merchant's client secret against the stored
/// BCrypt hash (ADR-0010). Tokens carry the merchant id as <c>sub</c> and a bounded lifetime; there
/// is no refresh flow — a merchant re-authenticates when the token expires.
/// </summary>
public sealed class TokenIssuanceService : ITokenIssuanceService
{
    private static readonly JsonWebTokenHandler TokenHandler = new();

    // A valid throwaway hash used to equalise BCrypt work when the client is unknown, so response
    // time can't leak whether a clientId exists (OWASP: protect against user enumeration).
    private static readonly string DummyHash = BCrypt.Net.BCrypt.HashPassword("credential-timing-equaliser");

    private readonly ICredentialCache _credentialCache;
    private readonly TimeProvider _timeProvider;
    private readonly SigningCredentials _signingCredentials;
    private readonly TimeSpan _tokenLifetime;

    public TokenIssuanceService(ICredentialCache credentialCache, TimeProvider timeProvider, string signingKey, TimeSpan tokenLifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        // HMAC-SHA256 requires a key of at least 256 bits; a shorter key throws only later, at sign
        // time, with an opaque message — reject it up front instead.
        if (Encoding.UTF8.GetByteCount(signingKey) < 32)
        {
            throw new ArgumentException(
                "The JWT signing key must be at least 256 bits (32 bytes) for HMAC-SHA256.", nameof(signingKey));
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(tokenLifetime, TimeSpan.Zero);

        _credentialCache = credentialCache;
        _timeProvider = timeProvider;
        _signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)), SecurityAlgorithms.HmacSha256);
        _tokenLifetime = tokenLifetime;
    }

    public async Task<IssuedToken?> IssueTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        var credential = await _credentialCache.GetByClientIdAsync(clientId, cancellationToken);

        // Always run BCrypt (against a dummy hash when the client is unknown) so the response time
        // is the same whether or not the clientId exists.
        var secretMatches = VerifySecret(clientSecret, credential?.HashedSecret ?? DummyHash);

        // Uniform failure: an unknown client and a wrong secret are indistinguishable to the caller,
        // so nothing reveals which client ids exist.
        if (credential is null || !secretMatches)
        {
            return null;
        }

        var issuedAt = _timeProvider.GetUtcNow().UtcDateTime;

        var descriptor = new SecurityTokenDescriptor
        {
            Claims = new Dictionary<string, object>
            {
                ["sub"] = credential.MerchantId,
                ["jti"] = Guid.NewGuid().ToString()
            },
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = issuedAt.Add(_tokenLifetime),
            SigningCredentials = _signingCredentials
        };

        var token = TokenHandler.CreateToken(descriptor);
        return new IssuedToken(token, (int)_tokenLifetime.TotalSeconds);
    }

    private static bool VerifySecret(string clientSecret, string hashedSecret)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(clientSecret, hashedSecret);
        }
        catch (BCrypt.Net.SaltParseException)
        {
            // A corrupt/mis-seeded stored hash is a verification failure (uniform 401), not a 500.
            return false;
        }
    }
}
