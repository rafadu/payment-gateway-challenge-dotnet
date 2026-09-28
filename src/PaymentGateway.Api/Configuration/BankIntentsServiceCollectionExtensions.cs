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
///
/// <para>Whether the two hosted services actually run is gated on
/// <c>BankIntents:RunBackgroundServices</c> (default <c>true</c>). The component test suite sets
/// it to <c>false</c> so booting the host under <c>WebApplicationFactory</c> doesn't start the
/// reconciler's Mongo-polling loop.</para>
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

        // The reconciler and cleanup service poll on startup and hit the intents repository
        // (Mongo in production) with no initial delay. That is correct for the running app, but
        // fatal for the WebApplicationFactory-based component tests: they boot the full host with
        // no Mongo, so the reconciler's first sweep blocks on the driver's server-selection
        // timeout (~30s) and eventually orphans the test host. Gating registration on
        // BankIntents:RunBackgroundServices (default true) lets the test assembly switch both
        // hosted services off in one place (see the test project's TestEnvironment module
        // initializer) without every factory having to strip them out. Integration tests exercise
        // the reconciler by calling IntentReconciliationLogic directly, so they never need the
        // hosted timers running.
        var runBackgroundServices = configuration.GetValue("BankIntents:RunBackgroundServices", true);
        if (runBackgroundServices)
        {
            services.AddHostedService<BankIntentReconciler>();
            services.AddHostedService<BankIntentCleanupService>();
        }

        return services;
    }
}
