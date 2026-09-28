# ADR-0012: Bank-side Idempotency-Key forwarding

## Status

Accepted

## Context

ADR-0003 protects the merchant→gateway boundary: a merchant that retries `POST /api/payments`
with the same `Idempotency-Key` after a transient 5xx gets the cached `201`/`400` response and
the bank is **not** called a second time. But that protection only applies to the merchant-side
filter's replay cache — once the gateway's `IdempotencyResourceFilter` releases the claim (which
it does on any `5xx`), a retry reaches the bank again. If the bank charges the first time and
the gateway returns `503` (because `Mongo.AddAsync` failed, or the gateway process restarted
mid-flight), the merchant's retry would call the bank a second time → double charge. Same gap
ADR-0001's "no automatic retry" was deliberately designed around, just one layer out.

The deeper question — "what if the bank authorized but the gateway never persisted the outcome" —
is the outbox pattern (§3.2 of `docs/post-payment-orchestration-improvements.md`, ADR-0013). This
ADR addresses the **strictly narrower** question: "what if both the bank and the gateway are
fine, but the merchant sees a `5xx` and retries?" The answer mirrors what real acquirers do:
have the bank itself dedupe on a key the merchant supplied.

## Decision

Forward the merchant's `Idempotency-Key` HTTP header verbatim to the bank on every gateway→bank
call. The bank (or the simulator) caches the response under that key and replays it on a
matching retry.

Mechanics:

- The merchant's `Idempotency-Key: K` value is read by `PaymentsController.CallerBankIdempotencyKey()`
  (verbatim, no namespacing — see Consequences for why), passed through the `IPaymentsHandler`
  decorator chain, and forwarded by `AcquiringBankClient` as the `Idempotency-Key` request header
  on the bank call.
- The bank (simulator, or real acquirer) honors the header: on a cache miss under key `K`, it
  processes the request normally and caches the response; on a cache hit under `K`, it replays
  the cached response without processing again. This is the same shape as Stripe / Adyen's
  bank-side idempotency semantics.
- **Only 2xx responses are cached.** A `400`, `503`, or any other 4xx/5xx is **not** cached — a
  retry after a transient bank failure must genuinely re-attempt, not replay the failure.
- When `IdempotencyKey` is absent (no merchant opt-in), the gateway sends no header and the bank
  has nothing to dedupe on. This is the unchanged behavior pre-ADR-0012.

The change is mechanical at the gateway: one extra optional parameter (`string?
idempotencyKey`) on `IAcquiringBankClient.ProcessPaymentAsync` and `IPaymentsHandler.ProcessPaymentAsync`,
forwarded through the existing decorator chain. No new components, no new abstraction.

## Consequences

- **Closes the merchant-retries-after-5xx gap.** A retry with the same `Idempotency-Key` reaches
  the bank's cache, gets the cached answer, and never re-charges. Same property as ADR-0003's
  merchant-side cache, just on the other side of the gateway.
- **Independent of §3.2's outbox (ADR-0013).** This ADR closes the retry case. The outbox
  closes the silent-crash case ("gateway crashed mid-flight, merchant never gets a response, never
  retries"). The two are complementary, not redundant.
- **Verbatim forwarding (no namespacing).** The merchant's `Idempotency-Key` value is forwarded
  as-is to the bank. The merchant→gateway filter (ADR-0003) already requires the key to be unique
  enough to be useful; the bank's namespace is opaque to merchants. Namespacing
  (`{merchantSub}:{K}`) would add a parameter to the controller without solving any actual
  collision risk for UUID-style keys. If namespacing ever becomes a concern (e.g. if the gateway
  fronts multiple acquirers that share a key space), it lives in the controller at the
  read-header step — one line.
- **In-memory simulator state.** The simulator's `state.idemCache` is in-memory only and is
  lost on simulator restart. For the assessment this matches reality (a real bank's
  idempotency cache is external to the gateway, and its persistence story is the bank's
  concern, not ours). Documented in the simulator file; flagged for ADR-0013's "remaining
  gap" list.
- **No TTL on the simulator cache.** Cache is unbounded; only entries that merchants actively
  send create growth. Acceptable for the assessment; flagged in ADR-0012's "follow-ups" if a
  real-world TTL is ever needed.
- **The remaining un-covered case is bank crashes between receive and respond.** Both
  ADR-0003 and this ADR depend on the bank processing the request at least once. A bank that
  crashes after receiving but before responding is unrepresentable in Mountebank and out of
  scope for this submission — the same answer ADR-0003 gives.

## Mountebank gotcha (preserved for future sessions)

When adding the cache-stub to `imposters/bank_simulator.ejs`, the obvious
`function(req, state) { ... }` declaration is wrong: Mountebank's actual injection signatures are

- **Predicate `inject`**: called as `(config, logger, imposterState)` — 3 args.
- **Response `inject`**: called as `(config, injectState, logger, done, imposterState)` — 5 args,
  with the `injectState` slot being a deprecated per-resolver scratchpad, **not** the imposter's
  persistent state.

Declaring `function(req, state)` silently binds `state` to `logger` (predicate) or to
`injectState` (response), neither of which is the persistent state. The imposter's actual state
lives on `config.state`. The right declaration is `function(config) { ... config.state.idemCache ... }`.

Same applies to `decorate` behaviors — the function is `(config, response, logger)` with
`config.state` being the persistent state. `decorate` works as long as the function reads from
`config.state`; writing to `config.state` from `decorate` was also unreliable in the same
authoring session (cache-hit predicate never matched even after a successful first POST), so
the simulator uses `inject` responses for the cache write rather than `decorate` behaviors.

## Implementation (for the diff)

- `IAcquiringBankClient.ProcessPaymentAsync(BankPaymentRequest request, string? idempotencyKey = null, CancellationToken ct = default)`
- `IPaymentsHandler.ProcessPaymentAsync(PostPaymentRequest request, string merchantId, string? bankIdempotencyKey = null, CancellationToken ct = default)`
- `AcquiringBankClient` switched from `PostAsJsonAsync` to `HttpRequestMessage` so the header
  can be attached conditionally on the parameter being non-blank.
- `PaymentsController.CallerBankIdempotencyKey()` reads `Request.Headers[IdempotencyResourceFilter.HeaderName]`.
- The `MetricsDecorator` and `AuditOutcomeDecorator` thread the new parameter through unchanged.
- `imposters/bank_simulator.ejs` gains one cache-hit stub (FIRST in the array) and a cache write
  inside the 2xx-stub response `inject`s.

Two integration tests prove end-to-end: same key twice → same `authorization_code` (cache
replay); two different keys → two different `authorization_code`s (cache is key-scoped).

## Follow-ups (not in this ADR)

- The `BankIntent` (outbox, ADR-0013) could optionally store the merchant's `Idempotency-Key`
  so a stale-Pending sweeper could safely re-call the bank with the same key, closing the
  "bank crashed between receive and respond" gap that neither this ADR nor ADR-0003 covers.
  Tracked as a separate decision in ADR-0013's "remaining gap" section.
- A real acquirer's idempotency cache has a TTL (commonly ~24h). The simulator's cache is
  unbounded; if that ever needs to change, the cleanup is a TTL check inside the cache-hit
  predicate's `inject` function.
