using FluentAssertions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Configuration;

namespace PaymentGateway.Api.Tests.Configuration;

public class JwtAuthenticationServiceCollectionExtensionsTests
{
    private const string ValidSigningKey = "dev-signing-key-that-is-definitely-long-enough-for-hs256";

    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public async Task Registers_the_JwtBearer_authentication_scheme()
    {
        var services = new ServiceCollection();
        services.AddJwtAuthentication(Config(("Jwt:SigningKey", ValidSigningKey)));

        await using var provider = services.BuildServiceProvider();
        var scheme = await provider.GetRequiredService<IAuthenticationSchemeProvider>()
            .GetSchemeAsync(JwtBearerDefaults.AuthenticationScheme);

        scheme.Should().NotBeNull();
        scheme!.Name.Should().Be(JwtBearerDefaults.AuthenticationScheme);
        scheme.HandlerType.Should().Be<JwtBearerHandler>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Throws_when_the_signing_key_is_missing_or_blank(string? signingKey)
    {
        var act = () => new ServiceCollection().AddJwtAuthentication(Config(("Jwt:SigningKey", signingKey)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Jwt:SigningKey*");
    }

    [Fact]
    public void Throws_at_startup_when_the_signing_key_is_too_short()
    {
        // A too-short key must fail fast at boot, not on the first authenticated request.
        var act = () => new ServiceCollection().AddJwtAuthentication(Config(("Jwt:SigningKey", "too-short")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*256*");
    }
}