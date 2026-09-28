using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// Stores and reconciles outbox records for <c>POST /api/payments</c> (§3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>). The handler writes a
/// <see cref="BankIntent"/> before the bank is called and updates it as the bank responds and the
/// <see cref="Payment"/> is materialized. A hosted reconciler polls for intents whose
/// <c>UpdatedAt</c> is older than a threshold and picks up the work the handler didn't get to
/// finish (typically because the gateway crashed mid-flow).
/// </summary>
public interface IBankIntentsRepository
{
    /// <summary>Persists a new <see cref="BankIntent"/>. Throws on duplicate id.</summary>
    Task AddAsync(BankIntent intent, CancellationToken cancellationToken = default);

    /// <summary>Returns the intent with the given id, or <c>null</c> if none is stored.</summary>
    Task<BankIntent?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the bank's adjudication on an intent and transitions it to
    /// <see cref="BankIntentStatus.Authorized"/> or <see cref="BankIntentStatus.Declined"/>
    /// based on <paramref name="response"/>. <paramref name="updatedAt"/> is stamped on the
    /// stored row so the reconciler's stale-detection uses the correct baseline.
    /// </summary>
    Task RecordOutcomeAsync(Guid id, BankPaymentResponse response, DateTime updatedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the intent as <see cref="BankIntentStatus.Reconciled"/> — the <see cref="Payment"/>
    /// has been materialized and the row is closed. The reconciler never sees this row again.
    /// </summary>
    Task MarkReconciledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the intent as <see cref="BankIntentStatus.Cancelled"/> — the bank call never
    /// reached the bank and no Payment will ever be created. Terminal state; the reconciler never
    /// sees this row again.
    /// </summary>
    Task MarkCancelledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bumps the intent's <c>Attempts</c> counter and stamps <c>UpdatedAt</c> with
    /// <paramref name="updatedAt"/>. Used by the reconciler when it observes a stale intent but
    /// cannot resolve it (e.g. a stale Pending — unsafe to retry the bank call without idempotency
    /// support, so we just bump and let ops investigate).
    /// </summary>
    Task IncrementAttemptsAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns intents whose <c>UpdatedAt</c> is older than <paramref name="staleAfter"/> ago and
    /// whose status is one the reconciler can act on (<see cref="BankIntentStatus.Pending"/>,
    /// <see cref="BankIntentStatus.Authorized"/>, <see cref="BankIntentStatus.Declined"/>).
    /// Terminal states (<see cref="BankIntentStatus.Reconciled"/>,
    /// <see cref="BankIntentStatus.Cancelled"/>) are excluded.
    /// </summary>
    Task<IReadOnlyList<BankIntent>> FindStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default);
}
