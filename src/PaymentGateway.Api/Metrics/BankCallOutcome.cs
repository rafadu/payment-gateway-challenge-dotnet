namespace PaymentGateway.Api.Metrics;

/// <summary>
/// Outcome of an acquiring-bank call, used as the <c>outcome</c> tag on
/// <c>payments.bank.call.duration</c> (ADR-0007). Distinguishes a definitive answer
/// (<see cref="Success"/>) from the "we don't know" availability failures ADR-0001 cares about —
/// a client-side <see cref="Timeout"/> versus any other <see cref="Error"/> — so bank availability
/// can be sized separately from latency. A bank <c>400</c> is kept in its own
/// <see cref="InvalidRequest"/> bucket rather than <see cref="Error"/>: it's a gateway-side
/// integration defect, not a bank-availability problem, so it must not inflate the failure-rate
/// signal a circuit breaker would be sized against.
/// </summary>
public enum BankCallOutcome
{
    Success,
    Timeout,
    Error,
    InvalidRequest
}
