# ADR-0010: Merchant JWT authentication, MongoDB-backed, in-memory read-through cache

## Status
Accepted

## Context
The assessment brief does not ask for merchant authentication, and the README's implementation
guidance explicitly favors simplicity over over-engineering. `docs/design.md` originally listed
merchant authentication/authorization as out of scope for that reason.

This ADR reverses that call as a deliberate engineering decision, not an assessment requirement:
today, `POST`/`GET /api/payments` are callable by anyone, and any caller can retrieve any
payment by ID. Adding auth (a) closes that access-control gap and (b) — once merchants are
distinguishable identities — lets `GET /api/payments/{id}` be scoped so a merchant can only
retrieve its own payments, which is a real safety property, not just gatekeeping.

This also isn't a fresh idea grafted on top of the existing design: ADR-0004 already identified
this exact need and deliberately deferred it —

> "Auth lookups sit on the hot path (every authenticated request), which argues for a caching
> layer (e.g. a short-lived cache of validated API keys) to avoid a database round-trip per
> request — noted as a follow-on, not built as part of this decision."

This ADR is that follow-on. It implements only the **merchant authN/authZ** half of ADR-0004's
MongoDB decision. The **audit trail** half of ADR-0004 remains out of scope — see the note added
to ADR-0004's Consequences.

## Decision

**MongoDB `merchants` collection** (first real external datastore in this submission's actual
build, not just the production-architecture narrative):
```json
{ "merchantId": "...", "clientId": "...", "hashedSecret": "...", "createdAt": "..." }
```
Seeded via a Mongo init script (`docker-entrypoint-initdb.d`) at container startup — no
registration endpoint, matching the "seeded, not built" scope already agreed for this feature.

**Credential hashing**: BCrypt. Only the hash is ever stored or cached — the plaintext secret
exists only for the duration of a single `/api/auth/token` request, to be compared against the
hash, and is never persisted or logged.

**`POST /api/auth/token`** (unauthenticated — this is the login step):
- Request: `{ "clientId": "...", "clientSecret": "..." }`
- Looks up `clientId` via `ICredentialCache` (read-through: cache miss → query MongoDB → on a
  hit, populate the cache with the **hashed** secret and `merchantId`, never the plaintext).
- Verifies the supplied secret against the hash. Success → issue a JWT. Failure (bad secret,
  unknown `clientId`) → `401 Unauthorized`, no distinction in the response between the two cases
  (don't leak whether a `clientId` exists).
- JWT: HMAC-signed with a key from configuration (`Jwt:SigningKey`), `sub` claim = `merchantId`,
  15-minute expiry, no refresh-token flow — a merchant just re-authenticates when it expires.

**`ICredentialCache`**: in-memory, backed by `IMemoryCache`, read-through in front of MongoDB.
(`IMemoryCache` rather than a hand-rolled `ConcurrentDictionary` so expiration and entry eviction
are handled by the framework, and so the swap to `IDistributedCache`/Redis in ADR-0011 stays a
same-shaped implementation change behind this interface.)
- **TTL: 4 hours, absolute from cache-write time, not sliding on access.** Absolute (rather than
  refreshed on every read) so a cached entry can't be kept alive indefinitely by sustained
  traffic — it always falls back to MongoDB at least once every 4 hours, which matters if a
  merchant's credential were ever rotated or revoked (no such endpoint exists yet, but the cache
  shouldn't assume one never will).
- Expiry is checked on read; an expired entry is treated as a miss and re-fetched from MongoDB.

**Payment ownership**: `Payment` gains a `MerchantId` field, set from the JWT `sub` claim when
`POST /api/payments` creates it. `PaymentsController` requires `Authorization: Bearer <jwt>` via
ASP.NET Core's JWT Bearer middleware — missing/invalid/expired token → `401`.
`GET /api/payments/{id}` returns **`404`, not `403`**, when the caller isn't the owner — consistent
with not revealing whether a payment exists to a merchant that doesn't own it.

## Consequences
- MongoDB becomes a real dependency of the actual submission (a new `docker-compose.yml` service,
  a driver package, connection configuration) — the first time this codebase's build footprint
  goes beyond in-memory stores and the bank simulator. This is a deliberate trade-off: it adds
  operational surface area to a challenge whose stated guidance favors simplicity, in exchange for
  closing a real access-control gap.
- Integration tests now depend on a running MongoDB instance alongside the bank simulator (both
  via `docker-compose up`).
- Existing and planned component tests for `POST`/`GET /api/payments` need a valid bearer token
  going forward — test fixtures need a way to mint one (e.g. a seeded test merchant + a real call
  to `/api/auth/token`, or a token signed directly with the test config's signing key). This is a
  test-authoring detail to resolve when those tests are written, not a design gap.
- Not implemented, and explicitly out of scope: merchant registration/credential-rotation
  endpoints, roles/scopes, refresh tokens, and rate limiting on `/api/auth/token` (so this
  endpoint currently has no brute-force protection — noted as a known limitation, same treatment
  as the other "known limitation, intentionally not solved here" notes in `docs/design.md`).
- The Redis evolution of `ICredentialCache` is documented separately in ADR-0011 and is **not**
  built as part of this ADR.
