using System.Collections.Concurrent;

namespace PaymentGateway.Api.Services;

/// <summary>
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>-backed <see cref="IIdempotencyStore"/>.
/// In-memory only; cleared on process restart (ADR-0003). Singleton — it owns no per-request
/// state, so a single instance serves all requests.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private enum ClaimState { InProgress, Completed }

    private sealed record Entry(string RequestHash, ClaimState State, CachedResponse? Cached);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public IdempotencyClaim TryClaim(string key, string requestHash)
    {
        if (_entries.TryGetValue(key, out var existing))
        {
            // Same key, but a different body. Reject so a misuse can't silently bind the key to
            // the wrong payload (ADR-0003).
            if (existing.RequestHash != requestHash)
            {
                return new IdempotencyClaim(IdempotencyClaimOutcome.HashMismatch, null);
            }

            return existing.State switch
            {
                ClaimState.InProgress => new IdempotencyClaim(IdempotencyClaimOutcome.InProgress, null),
                ClaimState.Completed => new IdempotencyClaim(IdempotencyClaimOutcome.Completed, existing.Cached),
                _ => throw new InvalidOperationException($"Unknown claim state for key '{key}'.")
            };
        }

        var candidate = new Entry(requestHash, ClaimState.InProgress, Cached: null);
        if (!_entries.TryAdd(key, candidate))
        {
            // Lost a race with another concurrent caller that just inserted the same key. Re-fetch
            // and decide based on the now-existing entry (no recursion risk: TryAdd succeeded for
            // them so we won't loop again on this path).
            return TryClaim(key, requestHash);
        }

        return new IdempotencyClaim(IdempotencyClaimOutcome.NewClaim, null);
    }

    public void Complete(string key, CachedResponse response)
    {
        var updated = _entries.AddOrUpdate(
            key,
            _ => throw new InvalidOperationException(
                $"Cannot complete idempotency key '{key}': no in-progress claim exists for it."),
            (_, existing) => existing with { State = ClaimState.Completed, Cached = response });

        if (updated.State != ClaimState.Completed)
        {
            // Defensive: would only happen if Complete raced with Release. Treat as a bug — the
            // contract says Complete is only called by the filter that holds the claim.
            throw new InvalidOperationException(
                $"Failed to transition idempotency key '{key}' to completed.");
        }
    }

    public void Release(string key) => _entries.TryRemove(key, out _);
}