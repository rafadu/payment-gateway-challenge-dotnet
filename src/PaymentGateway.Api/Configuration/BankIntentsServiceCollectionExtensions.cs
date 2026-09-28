using Microsoft.Extensions.Options;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Persistence;
using PaymentGateway.Api.Services;

namespace PaymentGateway.Api.Configuration;

/// <summary>
/// Registers the bank-intents outbox (§3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>, ADR-0013):
/// the <see cref="IBankIntentsRepository"/> implementation (Mongo-backed in production,
/// in-memory as a development/test fallback), the <see cref="IntentReconciliationLogic"/>, the
/// <see cref="BankIntentReconcilerOptions"/> binding, and the two hosted services that drive the
/// lifecycle on timers — <see cref="BankIntentReconciler"/> (recovers stale intents) and
/// <see cref="BankIntentCleanupService"/> (deletes aged Reconciled intents to bound the
/// collection's growth).
///
/// <para>The Mongo vs. in-memory choice is gated on
/// <c>BankIntents:UseInMemory</c> in configuration. Default is <c>false</c> (production-style
/// Mongo). Set to <c>true</c> in <c>appsettings.Development.json</c> or via env var to run without
/// Mongo (useful for spinning up the app on a laptop without docker-compose).</para>
/// </summary>
public static class BankIntentsServiceCollectionExtensions
{
    public static IServiceCollection AddBankIntents(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<BankIntentReconcilerOptions>(
            configuration.GetSection(BankIntentReconcilerOptions.SectionName));
        services.Configure<BankIntentsCleanupOptions>(
            configuration.GetSection(BankIntentsCleanupOptions.SectionName));

        services.AddSingleton(TimeProvider.System);

        var useInMemory = configuration.GetValue("BankIntents:UseInMemory", false);
        if (useInMemory)
        {
            services.AddSingleton<IBankIntentsRepository, InMemoryBankIntentsRepository>();
        }
        else
        {
            services.AddSingleton<IBankIntentsRepository, MongoBankIntentsRepository>();
        }

        services.AddSingleton<IntentReconciliationLogic>();
        services.AddHostedService<BankIntentReconciler>();
        services.AddHostedService<BankIntentCleanupService>();

        return services;
    }
}
