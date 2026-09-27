namespace PaymentGateway.Api.Services;

/// <summary>
/// Thrown when the acquiring bank cannot give a definitive authorized/declined answer — a
/// non-success HTTP status, a timeout, or a network failure. The gateway maps this to a
/// <c>503 Service Unavailable</c> and never mistakes "we don't know" for "declined" (design.md,
/// ADR-0001). It deliberately does <b>not</b> represent a caller-initiated cancellation.
/// </summary>
public sealed class BankUnavailableException : Exception
{
    public BankUnavailableException(string message) : base(message)
    {
    }

    public BankUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
