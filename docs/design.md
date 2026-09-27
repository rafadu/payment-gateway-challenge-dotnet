# Payment Gateway — Design & Assumptions

This document records the key design decisions and assumptions made while building the payment
gateway, as requested by the assessment instructions. Decisions with significant trade-offs are
split out into their own ADRs under `docs/adr/`.

## Architecture

```
AuthController
   │  POST /api/auth/token → 200 / 401
   ▼
ITokenIssuanceService
   ├──────────────► ICredentialCache (in-memory, read-through; ADR-0010, Redis: ADR-0011)
   │                     └────────► MongoDB `merchants` collection (miss fallback)
   └──────────────► JWT issuance (HMAC-signed, 15-minute expiry)

PaymentsController                          [requires Authorization: Bearer <jwt>]
   │  POST /api/payments  → 201 / 400 / 503
   │  GET  /api/payments/{id} → 200 / 404
   ▼
IPaymentsService (orchestration)
   │  maps request → bank request, calls the bank, maps the result,
   │  persists only Authorized/Declined outcomes, tagged with the caller's MerchantId
   ├──────────────► IAcquiringBankClient (typed HttpClient, bounded timeout)
   └──────────────► IPaymentsRepository (MongoDB; in-memory fake for tests)
```

Validation (FluentValidation) runs in front of the controller action via ASP.NET Core's standard
`[ApiController]` model-validation pipeline. An invalid request never reaches the service layer,
the bank client, or the repository.

`PaymentsController`'s two actions require a valid bearer token (ASP.NET Core JWT Bearer
authentication); `AuthController`'s token endpoint does not, since it's the login step itself.
See ADR-0010 for the full merchant-authentication design and ADR-0011 for the Redis evolution of
the credential cache (documented, not built).

## Request/response contract

`PostPaymentRequest` fields and validation rules map directly to the assessment's requirements
table:

| Field | Rule |
|---|---|
| CardNumber | required, 14–19 digits, numeric only |
| ExpiryMonth | required, 1–12 |
| ExpiryYear | required; ExpiryMonth+ExpiryYear combined must be in the future |
| Currency | required, exactly 3 chars, must be one of a configured allow-list (max 3 codes) |
| Amount | required, integer, **> 0** (extension beyond the literal spec — see ADR-0002) |
| Cvv | required, 3–4 digits, numeric only |

`PaymentResponse` (shared shape for both the POST-success and GET responses, since the spec's two
tables are identical): `Id`, `Status`, `CardNumberLastFour` (string, to preserve any leading
zero), `ExpiryMonth`, `ExpiryYear`, `Currency`, `Amount`.

**`Status` is only ever `Authorized` or `Declined` in a persisted/retrievable payment.** The
assessment's response tables for both POST-success and GET are explicitly scoped to "payments
successfully sent to the acquiring bank" and constrain `Status` to those two values — `Rejected`
never appears in that schema. This is the basis for the two decisions below.

A persisted `Payment` also carries a `MerchantId`, taken from the caller's JWT (see "Merchant
authentication" below) — it isn't part of the request/response body, since it's derived from the
caller's identity, not supplied by them.

## Merchant authentication

`POST`/`GET /api/payments` require `Authorization: Bearer <jwt>`. Merchants obtain a token from
`POST /api/auth/token` (unauthenticated) with `clientId`/`clientSecret`, validated against a
MongoDB-backed, in-memory-cached credential store. `GET /api/payments/{id}` returns `404` (not
`403`) if the caller isn't the payment's owner, so existence of another merchant's payment is
never revealed. Full design, rationale, and what's deliberately not built (registration/rotation
endpoints, roles/scopes, refresh tokens, rate limiting on the token endpoint) are in ADR-0010; the
Redis evolution of the credential cache is in ADR-0011.

This is a deliberate extension beyond the assessment's stated requirements, made explicit here
rather than silently assumed — see ADR-0010's Context for the reasoning.

## Status code / persistence mapping for POST /api/payments

| Outcome | HTTP status | Persisted? | Body |
|---|---|---|---|
| Authorized / Declined (bank responded) | `201 Created` | Yes | `PaymentResponse` |
| Rejected (validation failed, bank never called) | `400 Bad Request` | No | validation `ProblemDetails` |
| Bank unavailable (`503` or timeout) | `503 Service Unavailable` | No | `ProblemDetails` |

Rationale:
- **Rejected → 400, not persisted.** Fails fast (no repository write, no ID generation, no bank
  call) and keeps the payment ledger limited to real bank-adjudicated outcomes, matching the
  schema constraint above. A client retrying the same malformed request never creates duplicate
  records, since nothing was ever written.
- **Bank failure → 503, not mapped to Declined.** Reporting "Declined" when the bank was actually
  unreachable would misrepresent what happened — a merchant might tell a shopper "try a different
  card" for a card that was never evaluated, and it would corrupt decline-rate/fraud metrics a
  real gateway relies on. Distinguishing "definitively no" from "we don't know" is treated as a
  correctness requirement, not a nicety. See ADR-0001 for the resiliency approach around this.
- **No automatic retry of the bank call.** The bank simulator has no idempotency-key support, so
  retrying a call that may have already succeeded risks double-authorization — worse than
  surfacing a single failure. See ADR-0001 and ADR-0003.

**Known limitation, intentionally not solved here:** if the gateway's own call to the bank times
out, the bank may have processed it before the response was lost — an inherently ambiguous
outcome. A production system would need bank-side idempotency-key support plus a reconciliation
process to resolve this safely. Solving it fully is out of scope for this exercise; the
idempotency-key feature (ADR-0003) protects the merchant-to-gateway boundary but explicitly does
not close this deeper gap.

## Data handling

- The full card number and CVV are never persisted and never logged — only the last four digits
  of the card number reach the domain model (`Payment`) or any response body.
- `CardNumberLastFour` is stored as a `string`, not an `int`, to avoid silently dropping a leading
  zero (a bug present in the original scaffold).
- The bank's `authorization_code` is not part of the merchant-facing response schema (per the
  spec's tables) and is not stored.

## Bank integration

- `IAcquiringBankClient` is a typed `HttpClient` (via `IHttpClientFactory`), configured with a
  `BaseAddress` and bounded `Timeout` from configuration (`BankSimulator:BaseUrl`,
  `BankSimulator:TimeoutSeconds`) — avoids the socket-exhaustion pitfall of constructing
  `HttpClient` per call, and gives the timeout behavior decided in ADR-0001. Its consumer
  `PaymentsService` is registered **scoped**, not singleton: capturing a typed client in a
  singleton would pin one `HttpClient` for the process lifetime and defeat `IHttpClientFactory`'s
  handler rotation (stale DNS) — the very thing the factory exists to prevent. A `ServiceLifetimeTests`
  regression test locks this in.
- The bank's wire contract (`card_number`, `expiry_date` as `"MM/yyyy"`, `currency`, `amount`,
  `cvv`, snake_case) is modeled with its own DTOs, kept separate from the merchant-facing
  contract, so the two independent JSON conventions never leak into each other.
- A timeout, network failure, unreadable response body, or a non-success response **other than
  `400`** is translated into a `BankUnavailableException`, caught centrally and mapped to `503` —
  the "we don't know" outcome, never conflated with a decline.
- A `400 Bad Request` from the bank is handled separately, as an `InvalidBankRequestException`. A
  `400` means the request we sent was missing a required field; since merchant input is already
  validated before the bank is called, this signals a defect in how the gateway built the bank
  request, not a bank-availability problem. It is therefore an internal error (mapped to `500`),
  **not** a `503` — surfacing it as `503` would invite a merchant retry that could never succeed
  and would corrupt bank-availability metrics.

## Concurrency

- `IPaymentsRepository`'s production impl is `MongoPaymentsRepository` (`ReplaceOneAsync` with
  `IsUpsert = true` for idempotent writes; `Find().FirstOrDefaultAsync` for reads). The Mongo
  driver owns the connection pool and the operations are thread-safe. The in-memory
  `InMemoryPaymentsRepository` exists only as a unit-test fixture; it's
  `ConcurrentDictionary<Guid, Payment>`-backed (the scaffold's plain `List<T>` was not
  thread-safe — see Stage 2's history).
- The idempotency store (ADR-0003) uses the same `ConcurrentDictionary`-based approach, with
  `TryAdd` used to atomically claim a key and avoid a check-then-act race between two concurrent
  requests carrying the same key.

## Configuration

Supported currencies and the bank simulator's base URL/timeout are configuration-driven, not
hardcoded, so the "no more than 3 currency codes" constraint and the resiliency timeout live in
`appsettings.json` rather than in code:

```json
{
  "SupportedCurrencies": [ "GBP", "USD", "EUR" ],
  "BankSimulator": {
    "BaseUrl": "http://localhost:8080",
    "TimeoutSeconds": 5
  },
  "Mongo": {
    "ConnectionString": "mongodb://localhost:27017",
    "Database": "payment_gateway"
  },
  "Jwt": {
    "SigningKey": "...",
    "ExpiryMinutes": 15
  },
  "CredentialCache": {
    "TtlHours": 4
  }
}
```

## Testing strategy

- **Unit tests** (no I/O): validator rules, `PaymentsService` orchestration logic with a mocked
  `IAcquiringBankClient` and `IPaymentsRepository`, idempotency-store/filter behavior,
  `ICredentialCache` read-through/TTL behavior with a mocked Mongo dependency, token-issuance
  logic with a mocked cache.
- **Component tests**: `WebApplicationFactory`-driven, exercising the full ASP.NET pipeline for
  paths that never need the bank — validation-rejection responses, GET 200/404, and the
  missing/invalid/expired-token 401 paths on `PaymentsController`. These now need a way to mint a
  valid bearer token for the happy-path cases (e.g. a seeded test merchant), to be resolved when
  the tests are written.
- **Integration tests**: run against the real bank simulator **and MongoDB** containers
  (`docker-compose up`), covering the Authorized / Declined / bank-unavailable scenarios
  end-to-end against the actual Mountebank wire contract, plus the full
  `/api/auth/token` → bearer-token → `/api/payments` flow against real Mongo-backed credentials,
  rather than a mocked `HttpClient`/database. These are tagged so they can be distinguished from
  the always-run unit/component suite, and require both containers to be running first.
- **Architecture tests** (ADR-0008): NetArchTest-based rules that enforce the layered structure as
  build-failing invariants — dependency direction between Core/Ports/Adapters/Web, controllers
  depending on ports not concrete adapters, interface placement, and a PCI safety net that keeps
  raw card fields (`CardNumber`/`Cvv`) off every type except the merchant-request and bank-wire
  DTOs. Run in the normal `dotnet test` pass; no infrastructure needed.

## Explicitly out of scope

Nothing beyond the request/response flow, validation, persistence, bank integration, merchant
authentication, and idempotency described above is implemented in this codebase — this is a
scoped exercise, not a production-ready service. That includes rate limiting (including on the
token endpoint — see ADR-0010's Consequences), a circuit breaker around the bank client
(ADR-0001), a Redis-backed credential cache (ADR-0011), merchant
registration/credential-rotation/roles/refresh-tokens (ADR-0010), and full bank-side
idempotency/reconciliation for ambiguous timeouts (ADR-0003).

### Update — audit persistence implemented (Stage 11)

The audit-persistence half of the out-of-scope list above has now been implemented. One document
per request is written to the `audit_records` collection in the `payment_gateway` database by an
audit middleware (ADR-0004, audit half; see ADR-0004's "Update — audit trail half now
implemented (Stage 11)" for the full contract). The full PAN and CVV are never persisted. What
remains out of scope is the asynchronous audit pipeline, field-level encryption, and
correlation IDs / trace context — see ADR-0004 for the explicit non-goals.

### Update — custom metrics implemented (ADR-0007)

The custom-metrics item from the out-of-scope list above is now implemented. The gateway
instruments the four metrics ADR-0007 defines — `payments.processed.count` (tagged
`status`/`currency`), `payments.bank.call.duration` (histogram, tagged `acquirer`/`outcome`),
`payments.idempotency.replay.count`, and `payments.rejected.reason.count` (tagged by the failed
validation rule) — via the vendor-neutral `System.Diagnostics.Metrics` API, exported over
OpenTelemetry to a Prometheus scraping endpoint at `GET /metrics`. Two clarifications beyond the
ADR's text: the `acquirer` tag is the constant `"simulator"` (single acquirer; ADR-0006's
multi-acquirer routing stays out of scope), and a rejected request's `currency` tag is normalised
to a real 3-letter code or `"UNKNOWN"` so attacker-controlled input can't inflate tag cardinality.
The `/metrics` endpoint is unauthenticated, intended for scraping from a trusted network; a
production deployment would restrict it at the ingress/network layer. See ADR-0007's "Update —
implemented" section for the full contract.

These aren't oversights — they're reasoned decisions, written up in detail in
[`docs/production-architecture.md`](production-architecture.md) and ADR-0004 through ADR-0009 and
ADR-0011, covering what this system would need to actually run in production (a persistent store,
an async audit pipeline, multi-acquirer support, observability, architecture/performance test
tooling, CI/CD, and a distributed credential cache) and, just as importantly, in what order it
would make sense to build them. ADR-0010 (merchant JWT authentication) is the one exception to
this "documented, not built" pattern — it's implemented, per the rationale in its Context section.
