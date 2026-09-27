using FluentAssertions;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using PaymentGateway.Api.Configuration;
using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Clients;

namespace PaymentGateway.Api.Tests.Configuration;

public class BankClientServiceCollectionExtensionsTests
{
    private const string ValidBaseUrl = "http://localhost:8080";
    private const int ValidTimeoutSeconds = 5;

    private static IConfiguration Config(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [Fact]
    public void Registers_a_resolvable_acquiring_bank_client()
    {
        var services = new ServiceCollection();
        // The typed client depends on PaymentMetrics (ADR-0007), registered by AddObservability.
        services.AddObservability();
        services.AddBankClient(Config(
            ("BankSimulator:BaseUrl", ValidBaseUrl),
            ("BankSimulator:TimeoutSeconds", ValidTimeoutSeconds.ToString())));

        using var provider = services.BuildServiceProvider();

        provider.GetService<IAcquiringBankClient>().Should().BeOfType<AcquiringBankClient>();
    }

    [Fact]
    public void Uses_the_default_timeout_when_not_configured()
    {
        var services = new ServiceCollection();
        services.AddBankClient(Config(("BankSimulator:BaseUrl", ValidBaseUrl)));

        using var provider = services.BuildServiceProvider();

        // Force the typed client to actually build its underlying HttpClient so the Timeout
        // configuration is applied (it's lazy otherwise — GetService<IClient>() only returns
        // an uninitialised wrapper).
        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(IAcquiringBankClient));

        http.Timeout.Should().Be(TimeSpan.FromSeconds(ValidTimeoutSeconds));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("http//localhost:8080")]   // missing colon after scheme
    public void Throws_when_base_url_is_missing_blank_or_not_an_absolute_uri(string? baseUrl)
    {
        var act = () => new ServiceCollection().AddBankClient(Config(("BankSimulator:BaseUrl", baseUrl)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*BankSimulator:BaseUrl*");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void Throws_when_timeout_seconds_is_not_positive(string timeoutSeconds)
    {
        var act = () => new ServiceCollection().AddBankClient(Config(
            ("BankSimulator:BaseUrl", ValidBaseUrl),
            ("BankSimulator:TimeoutSeconds", timeoutSeconds)));

        act.Should().Throw<InvalidOperationException>().WithMessage("*TimeoutSeconds*");
    }
}