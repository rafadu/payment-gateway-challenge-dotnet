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
    /// <summary>
    /// Ensures any indexes the store needs for efficient querying exist. Best-effort and
    /// idempotent — safe to call repeatedly. Invoked once at startup (off the hot path and off the
    /// constructor) so construction never blocks on I/O. Stores with nothing to index (e.g. the
    /// in-memory implementation) treat this as a no-op.
    /// </summary>
    Task EnsureIndexesAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Deletes intents whose status is <see cref="BankIntentStatus.Reconciled"/> AND whose
    /// <c>UpdatedAt</c> is older than <paramref name="olderThan"/>. Returns the count of rows
    /// removed. Called by the cleanup hosted service (TTL) to keep the collection bounded —
    /// Reconciled intents have no further use once the Payment is materialized and the
    /// Idempotency-Key replay window has passed.
    /// </summary>
    Task<int> DeleteReconciledOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes intents whose status is <see cref="BankIntentStatus.Pending"/> AND whose
    /// <c>CreatedAt</c> is older than <paramref name="olderThan"/>. Returns the count of rows
    /// removed.
    ///
    /// <para>Uses <c>CreatedAt</c>, NOT <c>UpdatedAt</c>: the reconciler sweeper calls
    /// <see cref="IncrementAttemptsAsync"/> on every Pending intent it observes, which refreshes
    /// <c>UpdatedAt</c> on every pass (default 5s). If the cutoff used <c>UpdatedAt</c>, the
    /// sweeper's own bumps would keep the cutoff moving forward forever and Pending would
    /// never be eligible for cleanup — a 503'd request would accumulate as a Pending intent
    /// indefinitely. <c>CreatedAt</c> is set once at intent creation and never mutated, so it's
    /// the true age of the intent.</para>
    ///
    /// <para>Pending intents represent requests where the bank call failed (or the gateway crashed
    /// before it) — the merchant got a 5xx and either retried, gave up, or never came back. After
    /// the retention window, ops hasn't investigated, the merchant has moved on, and the row is
    /// just dead weight. Deleting it is the right move.</para>
    /// </summary>
    Task<int> DeletePendingOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default);
}
