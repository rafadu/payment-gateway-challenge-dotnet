using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Metrics;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Composes the <see cref="IPaymentsHandler"/> chain: the <see cref="ProcessPaymentHandler"/>
/// core (which talks to the bank and the repository) is wrapped by the
/// <see cref="MetricsDecorator"/> (records <c>payments.processed.count</c>) and the
/// <see cref="AuditOutcomeDecorator"/> (stamps the audit-outcome side-channel on the active
/// <c>HttpContext</c>). Outer decorators run first; the core runs last.
///
/// Each link is also registered as itself so a future test (or alternative composition) can
/// construct a partial chain without the full DI graph — the public IPaymentsHandler binding
/// is the only one the controller resolves.
/// </summary>
public static class PaymentsHandlerServiceCollectionExtensions
{
    public static IServiceCollection AddPaymentsHandler(this IServiceCollection services)
    {
        // The AuditOutcomeDecorator needs to stamp the active HttpContext.Items key — register the
        // framework accessor (it's opt-in; without this line the decorator fails to activate).
        services.AddHttpContextAccessor();

        services.AddScoped<ProcessPaymentHandler>();
        services.AddScoped<MetricsDecorator>();
        services.AddScoped<AuditOutcomeDecorator>();

        services.AddScoped<IPaymentsHandler>(sp =>
        {
            ProcessPaymentHandler core = sp.GetRequiredService<ProcessPaymentHandler>();
            IPaymentsHandler withMetrics = new MetricsDecorator(
                core,
                sp.GetRequiredService<PaymentMetrics>());
            return new AuditOutcomeDecorator(
                withMetrics,
                sp.GetRequiredService<IHttpContextAccessor>());
        });

        return services;
    }
}
