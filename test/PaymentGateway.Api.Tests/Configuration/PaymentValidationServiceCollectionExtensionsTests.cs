using FluentAssertions;

using FluentValidation;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Models.Requests;

namespace PaymentGateway.Api.Tests.Configuration;

public class PaymentValidationServiceCollectionExtensionsTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Registers_a_resolvable_validator_for_PostPaymentRequest()
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddPaymentValidation(Config(("SupportedCurrencies:0", "GBP")));

        using var provider = services.BuildServiceProvider();

        provider.GetService<IValidator<PostPaymentRequest>>().Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Throws_when_SupportedCurrencies_is_missing(string? empty)
    {
        var settings = empty is null
            ? Array.Empty<(string, string?)>()
            : new[] { ("SupportedCurrencies", empty) };

        var act = () => new ServiceCollection().AddPaymentValidation(Config(settings));

        act.Should().Throw<InvalidOperationException>().WithMessage("*SupportedCurrencies*");
    }
}