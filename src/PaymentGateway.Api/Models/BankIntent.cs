using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Models;

/// <summary>
/// Outbox record for a <c>POST /api/payments</c> request (§3.2 of
/// <c>docs/post-payment-orchestration-improvements.md</c>). Written before the bank is called,
/// updated when the bank responds, and marked <see cref="BankIntentStatus.Reconciled"/> once the
/// <see cref="Payment"/> is materialized. If the gateway crashes anywhere in between, the
/// reconciler (B3) picks up the stale row and materializes the Payment from it — closing the
/// "bank authorized, gateway forgot" gap.
///
/// <para><see cref="Id"/> is shared with the eventual <see cref="Payment.Id"/>, so the
/// sweeper-materialized Payment has the same id the cached Idempotency-Key response would
/// have used.</para>
/// </summary>
public record BankIntent
{
    public Guid Id { get; init; }

    /// <summary>
    /// Identifies the merchant that owns this intent — same value as <see cref="Payment.MerchantId"/>
    /// once materialized, taken from the caller's JWT <c>sub</c> at intent creation time (ADR-0010).
    /// </summary>
    public string MerchantId { get; init; } = string.Empty;

    /// <summary>Sanitized snapshot of the merchant request. Never carries PAN or CVV.</summary>
    public BankIntentRequest Request { get; init; } = null!;

    /// <summary>Bank's adjudication. Null until the bank responds (then set in one atomic update).</summary>
    public BankPaymentResponse? Response { get; init; }

    public BankIntentStatus Status { get; init; }

    /// <summary>When the intent was first written.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>
    /// Last time the intent was mutated. The reconciler's stale-detection uses this; incrementing
    /// <see cref="Attempts"/> also updates it so a sweeper-bump doesn't re-trigger another sweep.
    /// </summary>
    public DateTime UpdatedAt { get; init; }

    /// <summary>
    /// Number of times the reconciler has observed this intent (e.g. as a stale Pending). Exposed
    /// so ops dashboards can spot a Pending intent that's been seen many times without resolution
    /// — likely a bank-side issue or an abandoned merchant request.
    /// </summary>
    public int Attempts { get; init; }

    /// <summary>
    /// Builds a new <see cref="BankIntent"/> in the <see cref="BankIntentStatus.Pending"/> state.
    /// The caller supplies the id (so the eventual <see cref="Payment"/> shares it) and the
    /// <paramref name="now"/> timestamp (so the in-memory repo doesn't have to inject a clock for
    /// the factory).
    /// </summary>
    public static BankIntent StartPending(Guid id, string merchantId, BankIntentRequest request, DateTime now) =>
        new()
        {
            Id = id,
            MerchantId = merchantId,
            Request = request,
            Status = BankIntentStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
}
