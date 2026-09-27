using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using NSubstitute;

using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Tests.Configuration;

public class TokenIssuanceServiceCollectionExtensionsTests
{
    private const string ValidSigningKey = "dev-signing-key-that-is-definitely-long-enough-for-hs256";

    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    private static ServiceCollection ServicesWithCredentialCache()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ICredentialCache>());
        return services;
    }

    [Fact]
    public void Registers_a_resolvable_token_issuance_service()
    {
        var services = ServicesWithCredentialCache();
        services.AddTokenIssuance(Config(("Jwt:SigningKey", ValidSigningKey), ("Jwt:ExpiryMinutes", "15")));

        using var provider = services.BuildServiceProvider();

        provider.GetService<ITokenIssuanceService>().Should().BeOfType<TokenIssuanceService>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_when_the_signing_key_is_missing_or_blank(string? signingKey)
    {
        var act = () => new ServiceCollection().AddTokenIssuance(Config(("Jwt:SigningKey", signingKey)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:SigningKey*");
    }

    [Fact]
    public void Throws_at_startup_when_the_signing_key_is_too_short()
    {
        // A too-short key must fail fast at boot, not on the first /api/auth/token call.
        var act = () => new ServiceCollection().AddTokenIssuance(Config(("Jwt:SigningKey", "too-short")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*256*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Throws_when_expiry_minutes_is_not_positive(string expiryMinutes)
    {
        var config = Config(("Jwt:SigningKey", ValidSigningKey), ("Jwt:ExpiryMinutes", expiryMinutes));

        var act = () => new ServiceCollection().AddTokenIssuance(config);

        act.Should().Throw<InvalidOperationException>().WithMessage("*ExpiryMinutes*");
    }
}
