using Microsoft.Extensions.Logging;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Services;

/// <summary>
/// Pure logic that turns a stale <see cref="BankIntent"/> into either a persisted
/// <see cref="Payment"/> (Authorized/Declined intents), an attempt-bumped Pending (left for
/// ops per R-7), or a Reconciled catch-up (idempotent re-mark when the Payment exists but the
/// intent wasn't closed).
///
/// <para>Separated from the <c>BackgroundService</c> wrapper (B4) so it can be unit-tested
/// without a host. The wrapper owns the timer; this class owns the decision-making.</para>
/// </summary>
public sealed class IntentReconciliationLogic
{
    private readonly IBankIntentsRepository _intents;
    private readonly IPaymentsRepository _payments;
    private readonly ILogger<IntentReconciliationLogic> _logger;
    private readonly TimeProvider _clock;

    public IntentReconciliationLogic(
        IBankIntentsRepository intents,
        IPaymentsRepository payments,
        ILogger<IntentReconciliationLogic> logger,
        TimeProvider clock)
    {
        _intents = intents;
        _payments = payments;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>
    /// Reconciles every intent whose <c>UpdatedAt</c> is older than <paramref name="staleAfter"/>
    /// ago. Returns the number of intents that ended up Reconciled by this pass (whether newly
    /// materialised from an Authorized/Declined, or caught up against an existing Payment).
    /// </summary>
    public async Task<int> ReconcileStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        var stale = await _intents.FindStaleAsync(staleAfter, cancellationToken);
        var reconciled = 0;

        foreach (var intent in stale)
        {
            try
            {
                if (await ReconcileOneAsync(intent, cancellationToken))
                {
                    reconciled++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Caller asked to stop — propagate.
                throw;
            }
            catch (Exception ex)
            {
                // A single bad intent must not abort the whole pass — the sweeper is the only
                // thing that retries, so aborting on the first error means one bad intent
                // blocks every other intent behind it.
                _logger.LogWarning(ex, "Failed to reconcile bank intent {IntentId}; skipping.", intent.Id);
            }
        }

        return reconciled;
    }

    private async Task<bool> ReconcileOneAsync(BankIntent intent, CancellationToken cancellationToken)
    {
        // Step 1: is the Payment already there? Crash point 4 (write #4 failed after Payment
        // was persisted) leaves exactly this state. Idempotent catch-up: close the intent and
        // do nothing else — re-writing the Payment would be a double-bill on the merchant.
        var existingPayment = await _payments.GetAsync(intent.Id, cancellationToken);
        if (existingPayment is not null)
        {
            await _intents.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime, cancellationToken);
            return true;
        }

        switch (intent.Status)
        {
            case BankIntentStatus.Authorized:
            case BankIntentStatus.Declined:
                if (intent.Response is null)
                {
                    // Defensive: an Authorized/Declined intent without a stored response is
                    // impossible by construction (RecordOutcomeAsync is the only writer of
                    // Response and it always sets Status to Authorized/Declined). If it ever
                    // happens, treat as Pending — bump Attempts and let ops investigate.
                    _logger.LogWarning(
                        "Bank intent {IntentId} has status {Status} but no stored response; bumping Attempts.",
                        intent.Id, intent.Status);
                    await _intents.IncrementAttemptsAsync(intent.Id, _clock.GetUtcNow().UtcDateTime, cancellationToken);
                    return false;
                }

                // Reconstruct the Payment from the stored snapshot + response. The id is shared
                // (intent.Id) so the cached Idempotency-Key response and the sweeper-materialized
                // Payment reference the same id.
                var payment = Payment.FromBankOutcome(intent.MerchantId, intent.Request, intent.Response, intent.Id);
                await _payments.AddAsync(payment, cancellationToken);
                await _intents.MarkReconciledAsync(intent.Id, _clock.GetUtcNow().UtcDateTime, cancellationToken);
                return true;

            case BankIntentStatus.Pending:
                // R-7: never retry the bank call from the reconciler. This holds even with ADR-0012's
                // bank-side idempotency: the intent persists neither the Idempotency-Key nor the full
                // PAN (the snapshot is last-four only, by PCI design), so there's no key to replay and
                // no request body to safely resend — a fresh call could double-charge. Bump Attempts so
                // ops can see how long a Pending has been stuck; leave the status alone.
                await _intents.IncrementAttemptsAsync(intent.Id, _clock.GetUtcNow().UtcDateTime, cancellationToken);
                return false;

            default:
                // FindStaleAsync already filters Reconciled and Cancelled, so this branch is
                // unreachable. If a future status sneaks through, treat it as Pending (bump and
                // skip) rather than crash the loop.
                _logger.LogWarning(
                    "Bank intent {IntentId} has unexpected status {Status}; bumping Attempts.",
                    intent.Id, intent.Status);
                await _intents.IncrementAttemptsAsync(intent.Id, _clock.GetUtcNow().UtcDateTime, cancellationToken);
                return false;
        }
    }
}
