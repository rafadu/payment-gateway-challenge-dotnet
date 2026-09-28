using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using PaymentGateway.Api.Abstractions;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Runs <see cref="IBankIntentsRepository.EnsureIndexesAsync"/> once, shortly after the host starts.
/// This is where the <c>bank_intents</c> collection's <c>(Status, UpdatedAt)</c> index gets created
/// — deliberately here rather than in <c>MongoBankIntentsRepository</c>'s constructor so that
/// constructing the repository never blocks on a Mongo round-trip (the driver's server-selection
/// timeout is ~30s when Mongo is unreachable). As a <see cref="BackgroundService"/> the work happens
/// after startup completes, so a slow or unreachable Mongo delays only the index, not the app.
///
/// <para>Best-effort: failures are swallowed by <c>EnsureIndexesAsync</c> and logged there; without
/// the index <c>FindStaleAsync</c> still returns correct results, just via a collection scan.
/// Registered alongside the reconciler/cleanup under the <c>BankIntents:RunBackgroundServices</c>
/// flag, so the component-test suite (no Mongo) doesn't run it — see
/// <c>Configuration/BankIntentsServiceCollectionExtensions</c>.</para>
/// </summary>
public sealed class BankIntentIndexInitializer : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BankIntentIndexInitializer> _logger;

    public BankIntentIndexInitializer(IServiceProvider services, ILogger<BankIntentIndexInitializer> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Per-run scope: resolve the repository fresh rather than capturing it, matching the
        // reconciler/cleanup services (keeps the wrapper safe if the repo ever becomes scoped).
        await using var scope = _services.CreateAsyncScope();
        var intents = scope.ServiceProvider.GetRequiredService<IBankIntentsRepository>();

        try
        {
            await intents.EnsureIndexesAsync(stoppingToken);
            _logger.LogInformation("Bank intent index initialization complete.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host is shutting down before the index finished — nothing to do; it'll be attempted
            // again on the next start.
        }
    }
}
