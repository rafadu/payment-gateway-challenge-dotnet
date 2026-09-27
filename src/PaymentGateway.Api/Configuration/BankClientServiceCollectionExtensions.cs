using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the acquiring-bank HTTP client from the <c>BankSimulator</c> configuration section.
/// Typed <see cref="HttpClient"/> with bounded timeout (ADR-0001) — avoids socket exhaustion from
/// constructing <see cref="HttpClient"/> per call. The section is required at startup; only the
/// <c>TimeoutSeconds</c> value has a built-in default.
/// </summary>
public static class BankClientServiceCollectionExtensions
{
    private const double DefaultTimeoutSeconds = 5d;

    public static IServiceCollection AddBankClient(this IServiceCollection services, IConfiguration configuration)
    {
        var baseUrl = configuration.GetValue<string>("BankSimulator:BaseUrl");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException("Missing configuration: BankSimulator:BaseUrl.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"Configuration BankSimulator:BaseUrl is not a valid absolute URI: '{baseUrl}'.");
        }

        var timeoutSeconds = configuration.GetValue("BankSimulator:TimeoutSeconds", DefaultTimeoutSeconds);
        if (timeoutSeconds <= 0)
        {
            throw new InvalidOperationException("Configuration BankSimulator:TimeoutSeconds must be greater than zero.");
        }

        services.AddHttpClient<IAcquiringBankClient, AcquiringBankClient>(client =>
        {
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        });

        return services;
    }
}