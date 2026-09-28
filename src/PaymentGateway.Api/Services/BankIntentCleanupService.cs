using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PaymentGateway.Api.Abstractions;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Hosted service that periodically deletes <see cref="BankIntentsCleanupOptions.RetentionSeconds"/>-old
/// <see cref="BankIntentStatus.Reconciled"/> intents from the <c>bank_intents</c> collection.
/// Without this, Reconciled intents pile up forever — they're terminal from the reconciler's
/// perspective (no further action), but the collection's growth has no upper bound.
///
/// <para>Reconciles via <see cref="IBankIntentsRepository.DeleteReconciledOlderThanAsync"/>, which
/// uses the existing (Status, UpdatedAt) compound index — the delete is a bounded index range
/// scan, not a collection scan. Pending / Authorized / Declined / Cancelled intents are
/// deliberately untouched (cleanup is purely about bounding growth, not about reconciler state).</para>
///
/// <para>Errors during a pass are logged and the loop continues — a single bad pass mustn't block
/// future passes (same rationale as <see cref="BankIntentReconciler"/>).</para>
/// </summary>
public sealed class BankIntentCleanupService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly IOptions<BankIntentsCleanupOptions> _options;
    private readonly ILogger<BankIntentCleanupService> _logger;

    public BankIntentCleanupService(
        IServiceProvider services,
        IOptions<BankIntentsCleanupOptions> options,
        ILogger<BankIntentCleanupService> logger)
    {
        _services = services;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pollInterval = TimeSpan.FromSeconds(_options.Value.PollIntervalSeconds);
        var retention = TimeSpan.FromSeconds(_options.Value.RetentionSeconds);
        _logger.LogInformation(
            "Bank intent cleanup started (poll={PollInterval}s, retention={RetentionDays} days).",
            _options.Value.PollIntervalSeconds, retention.TotalDays);

        // Sleep first, then run cleanup. This matters at startup: firing a DeleteMany on Mongo
        // immediately would contend with the integration fixture's probe and with parallel
        // test-host startups. The retention is days/weeks and the poll is hours, so deferring
        // the first pass by one poll interval doesn't change which intents get deleted — any
        // Reconciled intent that's eligible was eligible yesterday and will be eligible tomorrow.
        while (!stoppingToken.IsCancellationRequested)
        {
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
                var (reconciled, pending) = await CleanupOnceAsync(retention, stoppingToken);
                if (reconciled > 0)
                {
                    _logger.LogInformation(
                        "Deleted {Count} Reconciled bank intent(s) older than {RetentionDays} days.",
                        reconciled, retention.TotalDays);
                }
                if (pending > 0)
                {
                    _logger.LogInformation(
                        "Deleted {Count} Pending bank intent(s) older than {RetentionDays} days (no merchant retry / no ops follow-up).",
                        pending, retention.TotalDays);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error during bank intent cleanup pass; will retry on the next interval.");
            }
        }

        _logger.LogInformation("Bank intent cleanup stopping.");
    }

    private async Task<(int Reconciled, int Pending)> CleanupOnceAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        // Per-pass scope so any future scoped dependency resolves fresh (today every dependency
        // is singleton, but the scope makes that evolution safe without a change here).
        await using var scope = _services.CreateAsyncScope();
        var intents = scope.ServiceProvider.GetRequiredService<IBankIntentsRepository>();
        var cutoff = DateTime.UtcNow - retention;
        // Two deletes: Reconciled (UpdatedAt-keyed, terminal state with no further use) and
        // Pending (CreatedAt-keyed, requests that bank-failed and the merchant/ops never acted on).
        // Both share the same retention. Authorized/Declined are deliberately untouched: those
        // mean the reconciler itself is broken and the row is a signal for ops, not dead weight.
        var reconciledDeleted = await intents.DeleteReconciledOlderThanAsync(cutoff, cancellationToken);
        var pendingDeleted = await intents.DeletePendingOlderThanAsync(cutoff, cancellationToken);
        return (reconciledDeleted, pendingDeleted);
    }
}
