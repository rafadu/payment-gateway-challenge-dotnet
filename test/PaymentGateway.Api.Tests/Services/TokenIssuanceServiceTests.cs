using System.Text;

using FluentAssertions;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using NSubstitute;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Services;

public class TokenIssuanceServiceTests
{
    private const string SigningKey = "dev-signing-key-that-is-definitely-long-enough-for-hs256";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string CorrectSecretHash = BCrypt.Net.BCrypt.HashPassword("correct-secret");

    private readonly ICredentialCache _cache = Substitute.For<ICredentialCache>();
    private readonly TokenIssuanceService _service;

    public TokenIssuanceServiceTests()
    {
        _service = new TokenIssuanceService(_cache, new FixedTimeProvider(Now), SigningKey, Lifetime);
    }

    private void CacheReturns(MerchantCredential? credential, string clientId = "client-1") =>
        _cache.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>()).Returns(credential);

    [Fact]
    public async Task Returns_null_for_an_unknown_client()
    {
        CacheReturns(null);

        (await _service.IssueTokenAsync("client-1", "correct-secret")).Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_secret_is_wrong()
    {
        CacheReturns(new MerchantCredential("merchant-42", "client-1", CorrectSecretHash));

        (await _service.IssueTokenAsync("client-1", "wrong-secret")).Should().BeNull();
    }

    [Fact]
    public async Task Issues_a_token_with_the_configured_lifetime_for_valid_credentials()
    {
        CacheReturns(new MerchantCredential("merchant-42", "client-1", CorrectSecretHash));

        var result = await _service.IssueTokenAsync("client-1", "correct-secret");

        result.Should().NotBeNull();
        result!.ExpiresInSeconds.Should().Be(900);
    }

    [Fact]
    public async Task Issued_token_is_signed_with_the_key_and_carries_the_merchant_id_as_sub()
    {
        CacheReturns(new MerchantCredential("merchant-42", "client-1", CorrectSecretHash));

        var result = await _service.IssueTokenAsync("client-1", "correct-secret");

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(result!.AccessToken, new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = false,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey))
        });

        validation.IsValid.Should().BeTrue();
        validation.Claims["sub"].Should().Be("merchant-42");
    }

    [Fact]
    public async Task Returns_null_when_the_stored_hash_is_malformed()
    {
        // A corrupt/mis-seeded hash must be a uniform 401, not an unhandled 500 that leaks which
        // clientId has a broken record.
        CacheReturns(new MerchantCredential("merchant-42", "client-1", "not-a-valid-bcrypt-hash"));

        (await _service.IssueTokenAsync("client-1", "correct-secret")).Should().BeNull();
    }

    [Fact]
    public async Task Issued_tokens_have_unique_jti_identifiers()
    {
        CacheReturns(new MerchantCredential("merchant-42", "client-1", CorrectSecretHash));

        var first = await _service.IssueTokenAsync("client-1", "correct-secret");
        var second = await _service.IssueTokenAsync("client-1", "correct-secret");

        var handler = new JsonWebTokenHandler();
        var firstJti = handler.ReadJsonWebToken(first!.AccessToken).GetClaim("jti").Value;
        var secondJti = handler.ReadJsonWebToken(second!.AccessToken).GetClaim("jti").Value;

        firstJti.Should().NotBeNullOrWhiteSpace();
        firstJti.Should().NotBe(secondJti);
    }

    [Fact]
    public async Task Issued_token_expires_at_now_plus_lifetime()
    {
        CacheReturns(new MerchantCredential("merchant-42", "client-1", CorrectSecretHash));

        var result = await _service.IssueTokenAsync("client-1", "correct-secret");

        var token = new JsonWebTokenHandler().ReadJsonWebToken(result!.AccessToken);
        token.ValidTo.Should().BeCloseTo((Now + Lifetime).UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Rejects_a_signing_key_shorter_than_256_bits()
    {
        var act = () => new TokenIssuanceService(_cache, new FixedTimeProvider(Now), "too-short", Lifetime);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rejects_a_non_positive_lifetime()
    {
        var act = () => new TokenIssuanceService(_cache, new FixedTimeProvider(Now), SigningKey, TimeSpan.Zero);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
