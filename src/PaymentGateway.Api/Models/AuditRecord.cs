namespace PaymentGateway.Api.Models;

/// <summary>
/// A single audit record (ADR-0004): one document per processed request. Captures the
/// minimum needed for forensics, dispute resolution, and a per-merchant view: who did what,
/// against which endpoint, with what outcome, in how long. The full PAN and CVV are never
/// persisted here — only the masked <see cref="RequestSummary"/>.
/// </summary>
public sealed class AuditRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>JWT <c>sub</c> claim of the caller; empty for unauthenticated requests (e.g. a 401 on <c>/api/auth/token</c> with bad credentials).</summary>
    public string MerchantId { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public int StatusCode { get; init; }

    /// <summary>
    /// Outcome label. <c>Authorized</c> / <c>Declined</c> for bank-adjudicated 201s;
    /// <c>ValidationRejected</c> for 400; <c>Unauthorized</c> for 401; <c>NotFound</c> for 404;
    /// <c>Conflict</c> for 409 (idempotency in-progress); <c>HashMismatch</c> for 422;
    /// <c>BankUnavailable</c> for 503; <c>InternalError</c> for 500. Producers can override via
    /// <c>HttpContext.Items["Audit.Outcome"]</c> before the response goes out — otherwise the
    /// filter derives a label from the status code.
    /// </summary>
    public string Outcome { get; init; } = string.Empty;

    public long DurationMs { get; init; }

    /// <summary>
    /// Masked body for write requests (currently POST /api/payments only). Always omits the CVV
    /// (PCI-DSS) and the full PAN — only the last four digits of the card number are retained.
    /// Null for read requests.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? RequestSummary { get; init; }
}
