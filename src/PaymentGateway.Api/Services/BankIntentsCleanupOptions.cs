namespace PaymentGateway.Api.Services;

/// <summary>
/// Bound from the <c>BankIntentsCleanup</c> configuration section. Defaults match what the
/// design discussion landed on: a once-an-hour pass and a 30-day retention (so a Reconciled
/// intent survives past the typical Idempotency-Key replay window and the usual merchant
/// support-ticket escalation path, then ages out).
/// </summary>
public class BankIntentsCleanupOptions
{
    public const string SectionName = "BankIntentsCleanup";

    /// <summary>Seconds between cleanup passes. Lower = tighter collection size, higher Mongo load.</summary>
    public int PollIntervalSeconds { get; set; } = 3600;

    /// <summary>A Reconciled intent is deleted once its <c>UpdatedAt</c> is older than this many seconds.</summary>
    public int RetentionSeconds { get; set; } = 30 * 24 * 3600;
}
