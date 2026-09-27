namespace PaymentGateway.Api.Metrics;

/// <summary>
/// Outcome of an acquiring-bank call, used as the <c>outcome</c> tag on
/// <c>payments.bank.call.duration</c> (ADR-0007). Distinguishes a definitive answer
/// (<see cref="Success"/>) from the two "we don't know" failure shapes ADR-0001 cares about —
/// a client-side <see cref="Timeout"/> versus any other <see cref="Error"/> — so bank
/// availability can be sized separately from latency.
/// </summary>
public enum BankCallOutcome
{
    Success,
    Timeout,
    Error
}
