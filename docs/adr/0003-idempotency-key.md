# ADR-0003: Idempotency-key support for POST /api/payments

## Status
Accepted

## Context
A merchant's request to `POST /api/payments` can fail to complete on their side (e.g. a network
timeout) without the merchant knowing whether the gateway actually processed it. If they retry
blindly, and the original request did complete, a naive retry would call the bank a second time
for what is logically the same purchase — the exact double-authorization risk already used to
justify *not* auto-retrying bank calls from the gateway itself (ADR-0001). The merchant needs a
safe way to retry.

The industry-standard mechanism for this (used by Stripe, Adyen, PayPal, etc.) is a
client-supplied idempotency key.

## Decision
Implement an **opt-in** `Idempotency-Key` HTTP header on `POST /api/payments`.

Mechanics:
- The header is a merchant-generated unique value (e.g. a UUID), one per distinct purchase
  attempt. It is metadata about the request attempt, not part of the payment payload, so it
  travels as a header rather than a body field.
- If the header is absent, the request is processed exactly as it would be without this feature
  — no behavior change for callers who don't opt in.
- If present, the gateway atomically claims the key (`ConcurrentDictionary.TryAdd`) alongside a
  hash of the normalized request body, before the bank is called:
  - **New key** → proceed normally. On completion:
    - Terminal outcome (`201` Authorized/Declined, or `400` Rejected) → the response is cached
      against the key.
    - Bank-unavailable (`503`) → the claim is **released**, not cached, since the failure is
      transient and the merchant should be able to reach the bank on a genuine retry once it
      recovers.
  - **Existing key, same request hash, still in progress** → `409 Conflict` (guards against two
    concurrent identical requests racing each other).
  - **Existing key, same request hash, already completed** → the cached response is replayed
    verbatim. **The bank is not called again.** This is the core safety property.
  - **Existing key, different request hash** → `422 Unprocessable Entity`. This catches a
    merchant accidentally reusing a key across two different payments, rather than silently
    returning the wrong payment's result.
- Implemented as an `IAsyncActionFilter` wrapping only the `CreatePayment` action (the GET
  endpoint is already safe to repeat and needs no such protection), backed by an in-memory
  `IIdempotencyStore`.

## Consequences
- Protects the gateway-to-merchant boundary: a merchant that retries correctly (same key, same
  payload) cannot cause a duplicate bank authorization through the gateway.
- **Does not** close the deeper ambiguous-outcome gap already flagged in the main design doc: if
  the *gateway's own* call to the bank times out, the bank may have already authorized the
  payment, and this simulator has no idempotency-key support of its own to dedupe on its side.
  Fully solving that would require bank-side idempotency support, which is out of scope here —
  it is a known, documented limitation, not a solved problem.
- No TTL/expiry is implemented for stored idempotency records, since the store is in-memory and
  is cleared on process restart anyway. A production system would need a TTL (commonly ~24h) and
  a persistent store, since idempotency keys must survive longer than a single process lifetime.
- Adds one interface (`IIdempotencyStore`), one in-memory implementation, and one action filter —
  scoped to stay proportionate to the rest of the challenge rather than building a general
  distributed idempotency system.
