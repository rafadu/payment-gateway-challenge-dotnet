namespace PaymentGateway.Api.Services;

/// <summary>
/// Bound from the <c>BankIntentReconciler</c> configuration section. Defaults match what the
/// design discussion landed on: a 5-second poll cadence and a 30-second stale threshold (so a
/// crash that interrupts write #3 of the outbox is reconciled within ~35s in the worst case).
/// </summary>
public class BankIntentReconcilerOptions
{
    public const string SectionName = "BankIntentReconciler";

    /// <summary>Seconds between reconciler passes. Lower = faster recovery, higher Mongo load.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>An intent is considered stale when its <c>UpdatedAt</c> is older than this many seconds.</summary>
    public int StaleAfterSeconds { get; set; } = 30;
}
