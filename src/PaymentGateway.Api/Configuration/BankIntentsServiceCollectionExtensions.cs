using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Persistence;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the bank-intents outbox (§3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>, ADR-0013).
///
/// <para>This slice registers the in-memory implementation as a placeholder so the handler chain
/// compiles and the existing component tests continue to pass. Slice B4 will swap this to the
/// Mongo-backed production implementation (<c>MongoBankIntentsRepository</c>), the (Status,
/// UpdatedAt) index, and the hosted reconciler.</para>
/// </summary>
public static class BankIntentsServiceCollectionExtensions
{
    public static IServiceCollection AddBankIntents(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IBankIntentsRepository, InMemoryBankIntentsRepository>();
        return services;
    }
}
