using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;

namespace PaymentGateway.Api.Persistence;

/// <summary>
/// In-memory audit store used by tests. Production code uses
/// <see cref="MongoAuditStore"/> via <c>AddAudit</c>.
/// </summary>
public sealed class InMemoryAuditStore : IAuditStore
{
    private readonly List<AuditRecord> _records = new();
    private readonly object _lock = new();

    public IReadOnlyList<AuditRecord> Records
    {
        get { lock (_lock) return _records.ToArray(); }
    }

    public Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        lock (_lock) _records.Add(record);
        return Task.CompletedTask;
    }
}
