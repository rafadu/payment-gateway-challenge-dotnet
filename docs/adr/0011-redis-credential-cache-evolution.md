# ADR-0011: Redis as the credential cache's distributed-cache evolution (design only)

## Status
Accepted — design only, not implemented in this submission.

## Context
ADR-0010's `ICredentialCache` is in-memory and per-instance. That's fine for a single process,
but has the same centralization gap already flagged for `IPaymentsRepository` and
`IIdempotencyStore`, and for the same reason ADR-0004/`docs/production-architecture.md` flag it
for those: under horizontal scaling, each instance independently misses and re-reads MongoDB for
the same merchant, and a cached entry on one instance isn't invalidated if a credential is
rotated on another (no rotation endpoint exists yet, but the cache design shouldn't assume one
never will).

Building the distributed version now would be speculative in the same way ADR-0001 ruled out a
circuit breaker: there's no real traffic/scaling data yet to justify it, and doing so would add
a Redis dependency (container, client library, connection config) to a submission whose stated
guidance favors simplicity, without a concrete need driving it yet.

## Decision
Document the swap; don't build it. When either (a) the service is actually scaled to more than
one instance, or (b) cross-instance credential-revalidation latency is measured as a problem,
replace `ICredentialCache`'s in-memory implementation with a Redis-backed one
(`IDistributedCache` via `StackExchange.Redis`), keeping the same read-through behavior and the
same 4-hour absolute TTL established in ADR-0010.

## Consequences
- No change to this submission's actual code, dependencies, or `docker-compose.yml`.
- `ICredentialCache` is the seam ADR-0010 already introduced specifically to make this swap
  possible later without touching callers (the token-issuance flow only depends on the
  interface, never on `ConcurrentDictionary` directly).
- Same category of "documented, not built" decision as ADR-0005 through ADR-0009.
