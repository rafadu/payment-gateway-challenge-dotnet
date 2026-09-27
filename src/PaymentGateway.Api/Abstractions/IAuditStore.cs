using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// Persists <see cref="AuditRecord"/> instances. One process-wide singleton — the audit
/// middleware awaits <see cref="WriteAsync"/> before returning the response, but the store
/// itself holds no per-request state.
/// </summary>
public interface IAuditStore
{
    Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default);
}
