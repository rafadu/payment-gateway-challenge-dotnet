namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// Shared constants for the audit contract (ADR-0004) between a request producer (e.g. a
/// controller) and the audit middleware. Lives in the ports layer so both the web tier and the
/// middleware can reference it without the controller taking a dependency on the middleware.
/// </summary>
public static class AuditConventions
{
    /// <summary>
    /// <c>HttpContext.Items</c> key a producer sets to override the audit outcome label the
    /// middleware would otherwise derive from the status code (e.g. Authorized vs Declined, which
    /// are both <c>201</c>).
    /// </summary>
    public const string OutcomeItemKey = "Audit.Outcome";
}
