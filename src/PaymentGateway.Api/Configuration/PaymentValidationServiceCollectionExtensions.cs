using FluentValidation;

using PaymentGateway.Api.Models.Requests;
using PaymentGateway.Api.Validation;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the <see cref="PostPaymentRequestValidator"/> from the <c>SupportedCurrencies</c>
/// configuration section (design.md). The allow-list comes from configuration rather than being
/// hardcoded so the "no more than 3 currency codes" assessment constraint is data, not code.
/// </summary>
public static class PaymentValidationServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentValidation(this IServiceCollection services, IConfiguration configuration)
    {
        var currencies = configuration.GetSection("SupportedCurrencies").Get<string[]>();
        if (currencies is null || currencies.Length == 0)
        {
            throw new InvalidOperationException(
                "Missing or empty configuration: SupportedCurrencies must contain at least one ISO currency code.");
        }

        services.AddSingleton<IValidator<PostPaymentRequest>>(sp =>
            new PostPaymentRequestValidator(currencies, sp.GetRequiredService<TimeProvider>()));

        return services;
    }
}