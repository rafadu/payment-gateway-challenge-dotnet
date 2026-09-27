namespace PaymentGateway.Api.Abstractions;

/// <summary>
/// Captured response that the <see cref="IIdempotencyStore"/> replays verbatim on a successful
/// retry with the same key and request hash (ADR-0003). Includes the headers that carry the
/// "shape" of the original response — the Location header on a 201 Created is part of that
/// contract, so it has to round-trip too.
/// </summary>
public sealed record CachedResponse(int StatusCode, string ContentType, string? Location, byte[] Body);

/// <summary>
/// Outcome of attempting to claim an idempotency key (ADR-0003):
/// <list type="bullet">
///   <item><see cref="NewClaim"/> — first time we see this key; the caller may proceed.</item>
///   <item><see cref="InProgress"/> — same key, same hash, still being processed; the caller
///   must wait or fail with 409.</item>
///   <item><see cref="Completed"/> — same key, same hash, already finished; replay the cached
///   response without re-running the action.</item>
///   <item><see cref="HashMismatch"/> — same key, but a different request hash; reject with 422
///   so the merchant can't silently bind the key to the wrong payload.</item>
/// </list>
/// </summary>
public enum IdempotencyClaimOutcome
{
    NewClaim,
    InProgress,
    Completed,
    HashMismatch
}

/// <summary>Result of an <see cref="IIdempotencyStore.TryClaim"/> call.</summary>
public sealed record IdempotencyClaim(IdempotencyClaimOutcome Outcome, CachedResponse? CachedResponse);

/// <summary>
/// In-memory store for idempotency-key claims. Concurrent-safe: a key is claimed atomically
/// (<c>ConcurrentDictionary.TryAdd</c>), so two simultaneous identical requests race at exactly
/// one boundary and the loser sees <see cref="IdempotencyClaimOutcome.InProgress"/>. A production
/// system would back this with a TTL (~24 h) and a persistent store — out of scope for this
/// exercise (ADR-0003).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Attempts to claim <paramref name="key"/> for a request with the given
    /// <paramref name="requestHash"/>. See <see cref="IdempotencyClaimOutcome"/> for outcomes.
    /// </summary>
    IdempotencyClaim TryClaim(string key, string requestHash);

    /// <summary>
    /// Transitions an in-progress claim to completed, caching <paramref name="response"/> for
    /// future replays. Throws if the claim does not exist (a caller bug, never a runtime state —
    /// the filter only calls this on a key it just successfully claimed).
    /// </summary>
    void Complete(string key, CachedResponse response);

    /// <summary>
    /// Removes the claim entirely — used when the upstream call failed transiently (e.g. 503 from
    /// the bank) and the merchant should be allowed to retry the same key once the cause clears.
    /// </summary>
    void Release(string key);
}