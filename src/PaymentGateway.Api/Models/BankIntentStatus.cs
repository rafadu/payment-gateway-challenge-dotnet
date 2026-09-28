namespace PaymentGateway.Api.Models;

/// <summary>
/// Lifecycle of an outbox record (see <c>docs/post-payment-orchestration-improvements.md</c> §3.2).
/// The pending states (<see cref="Pending"/>, <see cref="Authorized"/>, <see cref="Declined"/>)
/// are terminal for the request handler (it returns to the merchant) but **transient** for the
/// reconciler (they're the rows the sweeper picks up). The two reconciler-terminal states
/// (<see cref="Reconciled"/>, <see cref="Cancelled"/>) exclude the row from
/// <c>FindStaleAsync</c>.
/// </summary>
public enum BankIntentStatus
{
    /// <summary>Intent written, bank call not yet attempted or in flight.</summary>
    Pending,

    /// <summary>Bank responded "yes" — awaiting Payment materialization.</summary>
    Authorized,

    /// <summary>Bank responded "no" — awaiting Payment materialization.</summary>
    Declined,

    /// <summary>Payment successfully created and intent closed.</summary>
    Reconciled,

    /// <summary>Intent abandoned — bank call never reached the bank (e.g. client disconnect).</summary>
    Cancelled,
}
