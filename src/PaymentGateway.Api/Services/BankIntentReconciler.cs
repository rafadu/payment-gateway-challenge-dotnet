using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Abstractions;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Hosted wrapper that drives <see cref="IntentReconciliationLogic"/> on a timer (§3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>, ADR-0013). Each pass:
/// <list type="bullet">
///   <item>waits <see cref="BankIntentReconcilerOptions.PollIntervalSeconds"/> first (sleep-first,
///     symmetric with <see cref="BankIntentCleanupService"/>) so the initial pass doesn't fire
///     during host warm-up;</item>
///   <item>creates a scope so scoped dependencies are fresh per pass (defensive — currently
///     every dependency is singleton, but the per-pass scope keeps that flexible);</item>
///   <item>resolves the logic and calls <see cref="IntentReconciliationLogic.ReconcileStaleAsync"/>;</item>
///   <item>logs the count of reconciled intents (zero-count passes are not logged — quiet is the
///     common case).</item>
/// </list>
///
/// <para>Errors during a pass are logged but do not stop the loop — the sweeper is the only
/// thing that retries, so aborting on the first error means one bad pass blocks every future
/// pass. Cancellation propagates immediately when <paramref name="stoppingToken"/> is signalled.</para>
/// </summary>
public sealed class BankIntentReconciler : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<BankIntentReconcilerOptions> _options;
    private readonly ILogger<BankIntentReconciler> _logger;

    public BankIntentReconciler(
        IServiceProvider services,
        IOptions<BankIntentReconcilerOptions> options,
        ILogger<BankIntentReconciler> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(_options.Value.PollIntervalSeconds);
        var staleAfter = TimeSpan.FromSeconds(_options.Value.StaleAfterSeconds);
        _logger.LogInformation(
            "Bank intent reconciler started (poll={PollInterval}s, staleAfter={StaleAfter}s).",
            _options.Value.PollIntervalSeconds, _options.Value.StaleAfterSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Sleep first, then reconcile — symmetric with BankIntentCleanupService, and it keeps
            // the reconciler from hitting Mongo the instant the process starts (host warm-up).
            // staleAfter (default 30s) is well above one poll interval (default 5s), so deferring
            // the first pass reconciles the same intents it otherwise would have.
            try
            {
                await Task.Delay(pollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var reconciled = await ReconcileOnceAsync(staleAfter, stoppingToken);
                if (reconciled > 0)
                {
                    _logger.LogInformation(
                        "Reconciled {Count} stale bank intent(s) in this pass.", reconciled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error during bank intent reconciliation pass; will retry on the next interval.");
            }
        }

        _logger.LogInformation("Bank intent reconciler stopping.");
    }

    private async Task<int> ReconcileOnceAsync(TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        // Per-pass scope so any future scoped dependency on the reconciler resolves fresh. Today
        // IntentReconciliationLogic is singleton (all its deps are singleton), but the scope
        // makes the wrapper safe for that evolution without a code change here.
        await using var scope = _services.CreateAsyncScope();
        var logic = scope.ServiceProvider.GetRequiredService<IntentReconciliationLogic>();
        return await logic.ReconcileStaleAsync(staleAfter, cancellationToken);
    }
}
