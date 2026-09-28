using System.Collections.Concurrent;

using PaymentGateway.Api.Abstractions;
using PaymentGateway.Api.Models;
using PaymentGateway.Api.Models.Bank;

namespace PaymentGateway.Api.Persistence;

/// <summary>
/// In-memory <see cref="IBankIntentsRepository"/> backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Used by unit tests as a fake — production code uses <c>MongoBankIntentsRepository</c>
/// (registered in <c>Configuration/BankIntentsServiceCollectionExtensions</c>). Singleton — it owns
/// the dictionary, no per-request state.
///
/// <para>The constructor takes a <see cref="TimeProvider"/> so the reconciler's stale-detection
/// is testable without monkey-patching <see cref="DateTime.UtcNow"/>. Tests use
/// <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>.</para>
/// </summary>
public sealed class InMemoryBankIntentsRepository : IBankIntentsRepository
{
    private readonly ConcurrentDictionary<Guid, BankIntent> _intents = new();
    private readonly TimeProvider _clock;

    public InMemoryBankIntentsRepository(TimeProvider clock) =>
        _clock = clock;

    // No indexes to build for a ConcurrentDictionary — nothing to do.
    public Task EnsureIndexesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task AddAsync(BankIntent intent, CancellationToken cancellationToken = default)
    {
        if (!_intents.TryAdd(intent.Id, intent))
        {
            throw new InvalidOperationException(
                $"A BankIntent with id {intent.Id} already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<BankIntent?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_intents.TryGetValue(id, out var intent) ? intent : null);

    public Task RecordOutcomeAsync(
        Guid id,
        BankPaymentResponse response,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (!_intents.TryGetValue(id, out var existing) || existing is null)
        {
            // The handler records outcomes against ids it just created; the reconciler doesn't
            // call this. A no-op (rather than throw) is the safer contract — see interface XML doc.
            return Task.CompletedTask;
        }

        var newStatus = response.Authorized ? BankIntentStatus.Authorized : BankIntentStatus.Declined;
        _intents[id] = existing with
        {
            Status = newStatus,
            Response = response,
            UpdatedAt = updatedAt
        };
        return Task.CompletedTask;
    }

    public Task MarkReconciledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        if (_intents.TryGetValue(id, out var existing) && existing is not null)
        {
            _intents[id] = existing with
            {
                Status = BankIntentStatus.Reconciled,
                UpdatedAt = updatedAt
            };
        }

        return Task.CompletedTask;
    }

    public Task MarkCancelledAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        if (_intents.TryGetValue(id, out var existing) && existing is not null)
        {
            _intents[id] = existing with
            {
                Status = BankIntentStatus.Cancelled,
                UpdatedAt = updatedAt
            };
        }

        return Task.CompletedTask;
    }

    public Task IncrementAttemptsAsync(Guid id, DateTime updatedAt, CancellationToken cancellationToken = default)
    {
        if (_intents.TryGetValue(id, out var existing) && existing is not null)
        {
            _intents[id] = existing with
            {
                Attempts = existing.Attempts + 1,
                UpdatedAt = updatedAt
            };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BankIntent>> FindStaleAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - staleAfter;
        IReadOnlyList<BankIntent> stale = _intents.Values
            .Where(i => i.Status is BankIntentStatus.Pending
                            or BankIntentStatus.Authorized
                            or BankIntentStatus.Declined
                     && i.UpdatedAt < cutoff)
            .ToList();
        return Task.FromResult(stale);
    }

    public Task<int> DeleteReconciledOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        var victims = _intents
            .Where(kv => kv.Value.Status == BankIntentStatus.Reconciled && kv.Value.UpdatedAt < olderThan)
            .Select(kv => kv.Key)
            .ToList();
        var deleted = 0;
        foreach (var id in victims)
        {
            if (_intents.TryRemove(id, out _))
            {
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }

    public Task<int> DeletePendingOlderThanAsync(DateTime olderThan, CancellationToken cancellationToken = default)
    {
        // Pending uses CreatedAt (not UpdatedAt) so the sweeper's per-pass bumps don't keep the
        // cutoff moving forward — see XML doc on the interface for the rationale.
        var victims = _intents
            .Where(kv => kv.Value.Status == BankIntentStatus.Pending && kv.Value.CreatedAt < olderThan)
            .Select(kv => kv.Key)
            .ToList();
        var deleted = 0;
        foreach (var id in victims)
        {
            if (_intents.TryRemove(id, out _))
            {
                deleted++;
            }
        }

        return Task.FromResult(deleted);
    }
}
